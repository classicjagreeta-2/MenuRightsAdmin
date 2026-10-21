using System.Data;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;

namespace MenuRightsAdmin;

// All database work. Tables (run as ClassicNew):
//   MODULE                    one row per module: MENU_TABLE, USRACS_TABLE, MENU_SORT_BY (PID of top-level rows)
//   <MENU_TABLE>              the menu tree: ID + INDEX_ORA identify a node, PID = "<parent ID>!<parent INDEX_ORA>"
//   VBMENU_DONE               ORDERCODE (MENUCODE) -> MVC CONTROLLER/ACTIONMETHOD, per MODULECODE
//   <schema>.<USRACS_TABLE>   rights: one row per user per node (MNUID = node ID); no row = no access
//   USER_SCHEMA_LINK          which users are linked to which company schema for a module
public sealed partial class MenuStore(IConfiguration config)
{
    private readonly string _cs = config.GetConnectionString("ClassicNew")
        ?? throw new InvalidOperationException("ConnectionStrings:ClassicNew is not set.");

    // Table/schema names come from MODULE and USER_SCHEMA_LINK and are concatenated into SQL,
    // so they must be plain Oracle identifiers.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_$#]{0,127}$")] private static partial Regex IdentRx();
    [GeneratedRegex(@"^[0-9]{8}[A-Z]$")] private static partial Regex OrderCodeRx();
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,10}$")] private static partial Regex MenuIdRx();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,99}$")] private static partial Regex ControllerRx();

    // VBMENU_DONE.CONTROLLER is the MVC route name: "CultivationNirikEntry", not "...Controller".
    private static string StripSuffix(string controller) =>
        controller.Length > 10 && controller.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? controller[..^10] : controller;

    private static string Ident(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name) || !IdentRx().IsMatch(name.Trim()))
            throw new UserError($"{what} '{name}' is not a valid table/schema name.");
        return name.Trim().ToUpperInvariant();
    }

    private async Task<OracleConnection> OpenAsync()
    {
        var conn = new OracleConnection(_cs);
        await conn.OpenAsync();
        return conn;
    }

    private static OracleCommand Cmd(OracleConnection conn, string sql, OracleTransaction? tx = null)
    {
        var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = sql;
        if (tx != null) cmd.Transaction = tx;
        return cmd;
    }

    private static string? S(object v) => v is DBNull or null ? null : Convert.ToString(v)?.Trim();
    private static int? N(object v) => v is DBNull or null ? null : Convert.ToInt32(v);

    // ---- Modules -------------------------------------------------------------------------

    public async Task<List<ModuleInfo>> GetModulesAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = Cmd(conn,
            "SELECT m.ID, m.MODULE_NAME, m.MODULE_CODE, m.MENU_TABLE, m.USRACS_TABLE, m.MENU_SORT_BY, " +
            // a module whose menu table was never created (e.g. Head Office VBMENU_HP_PAYROLL) is not set up
            "       CASE WHEN EXISTS (SELECT 1 FROM ALL_TAB_COLUMNS c " +
            "                          WHERE c.OWNER = SYS_CONTEXT('USERENV','CURRENT_SCHEMA') " +
            "                            AND c.TABLE_NAME = UPPER(TRIM(m.MENU_TABLE))) THEN 1 ELSE 0 END " +
            "  FROM MODULE m " +
            " WHERE m.MENU_TABLE IS NOT NULL AND m.USRACS_TABLE IS NOT NULL ORDER BY m.MODULE_NAME");
        var list = new List<ModuleInfo>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new ModuleInfo(Convert.ToInt32(r[0]), S(r[1]) ?? "", S(r[2]) ?? "", S(r[3]) ?? "", S(r[4]) ?? "", S(r[5]) ?? "",
                                    Convert.ToInt32(r[6]) == 1));
        return list;
    }

    private static async Task<ModuleInfo> ModuleAsync(OracleConnection conn, int moduleId, OracleTransaction? tx = null)
    {
        await using var cmd = Cmd(conn,
            "SELECT ID, MODULE_NAME, MODULE_CODE, MENU_TABLE, USRACS_TABLE, MENU_SORT_BY FROM MODULE WHERE ID = :id", tx);
        cmd.Parameters.Add("id", moduleId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new UserError($"Module {moduleId} not found.");
        var m = new ModuleInfo(Convert.ToInt32(r[0]), S(r[1]) ?? "", S(r[2]) ?? "", S(r[3]) ?? "", S(r[4]) ?? "", S(r[5]) ?? "");
        Ident(m.MenuTable, "MENU_TABLE");
        Ident(m.UsracsTable, "USRACS_TABLE");
        if (string.IsNullOrEmpty(m.Code)) throw new UserError("Module has no MODULE_CODE.");
        if (string.IsNullOrEmpty(m.Root)) throw new UserError("Module has no MENU_SORT_BY (top-level PID).");
        return m;
    }

    // Columns that exist on a table - module tables were created at different times and
    // do not all carry the same optional columns (IMAGE, TAG, MENUTAG, DDAY ...).
    private static async Task<HashSet<string>> ColumnsAsync(OracleConnection conn, string? owner, string table, OracleTransaction? tx = null)
    {
        await using var cmd = Cmd(conn, owner == null
            ? "SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS WHERE OWNER = SYS_CONTEXT('USERENV','CURRENT_SCHEMA') AND TABLE_NAME = :t"
            : "SELECT COLUMN_NAME FROM ALL_TAB_COLUMNS WHERE OWNER = :o AND TABLE_NAME = :t", tx);
        if (owner != null) cmd.Parameters.Add("o", owner);
        cmd.Parameters.Add("t", table);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        if (set.Count == 0) throw new UserError($"Table {(owner == null ? "" : owner + ".")}{table} not found or not visible to this login.");
        return set;
    }

    // ---- Menu tree -----------------------------------------------------------------------

    public async Task<MenuView> GetMenuAsync(int moduleId)
    {
        await using var conn = await OpenAsync();
        var m = await ModuleAsync(conn, moduleId);
        return new MenuView(m, await MenuNodesAsync(conn, m));
    }

    private static async Task<List<MenuNode>> MenuNodesAsync(OracleConnection conn, ModuleInfo m, OracleTransaction? tx = null)
    {
        var cols = await ColumnsAsync(conn, null, m.MenuTable, tx);
        string typeCol = cols.Contains("TYPE") ? "m.TYPE" : "NULL";
        await using var cmd = Cmd(conn,
            $"SELECT m.ID, m.INDEX_ORA, m.PID, m.NAME, {typeCol}, m.ORDERCODE, m.PERDOTNETMENU, m.ATVDOTNETMENU, " +
            "       d.CONTROLLER, d.ACTIONMETHOD, d.ISACTIVE " +
            $"  FROM {m.MenuTable} m " +
            "  LEFT JOIN VBMENU_DONE d ON d.MODULECODE = :code AND d.MENUCODE = m.ORDERCODE " +
            " WHERE m.MODULE = :code ORDER BY m.ORDERCODE", tx);
        cmd.Parameters.Add("code", m.Code);
        var list = new List<MenuNode>();
        var seen = new HashSet<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var node = new MenuNode(S(r[0]) ?? "", S(r[1]) ?? "", S(r[2]) ?? "", S(r[3]) ?? "", S(r[4]),
                S(r[5]) ?? "", S(r[6]) == "Y", S(r[7]) == "Y", S(r[8]), S(r[9]), N(r[10]) == 1);
            if (seen.Add(node.Key)) list.Add(node);   // VBMENU_DONE should have one row per code; ignore extras
        }
        return list;
    }

    // Next ORDERCODE / ID / INDEX_ORA for a new child of the parent with key ID!INDEX_ORA (null = top level).
    // ORDERCODE convention: 8 digits + letter = 4 levels x 2 digits, unused levels '00'.
    public async Task<Suggestion> SuggestAsync(int moduleId, string? parentKey)
    {
        await using var conn = await OpenAsync();
        var m = await ModuleAsync(conn, moduleId);
        var nodes = await MenuNodesAsync(conn, m);
        return Suggest(m, nodes, parentKey);
    }

    private static Suggestion Suggest(ModuleInfo m, List<MenuNode> nodes, string? parentKey)
    {
        MenuNode? parent = null;
        if (!string.IsNullOrWhiteSpace(parentKey))
            parent = nodes.FirstOrDefault(n => n.Key == parentKey)
                     ?? throw new UserError($"Parent {parentKey} not found.");
        string pid = parent?.Key ?? m.Root;

        var siblings = nodes.Where(n => n.Pid == pid).ToList();
        string? id = siblings.GroupBy(n => n.Id).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        // Parent prefix: its code up to the last non-zero pair.
        string suffix = parent?.OrderCode.Length == 9 ? parent.OrderCode[8..] : (nodes.FirstOrDefault()?.OrderCode is { Length: 9 } c ? c[8..] : "T");
        string prefix = "";
        if (parent != null && parent.OrderCode.Length >= 8)
        {
            string digits = parent.OrderCode[..8].TrimEnd('0');
            prefix = parent.OrderCode[..((digits.Length + 1) / 2 * 2)];
        }

        string? orderCode = null;
        if (prefix.Length <= 6)
        {
            // digits only - the same position may be used with another letter (01011000F)
            var used = nodes.Where(n => n.OrderCode.Length >= 8).Select(n => n.OrderCode[..8]).ToHashSet();
            int max = nodes.Where(n => n.OrderCode.Length == 9 && n.OrderCode.StartsWith(prefix)
                                       && n.OrderCode.Substring(prefix.Length + 2, 6 - prefix.Length).All(ch => ch == '0'))
                           .Select(n => int.TryParse(n.OrderCode.Substring(prefix.Length, 2), out var v) ? v : 0)
                           .DefaultIfEmpty(0).Max();
            // After the last child; when 99 is taken, the first free gap.
            foreach (int next in Enumerable.Range(max + 1, Math.Max(0, 99 - max)).Concat(Enumerable.Range(1, Math.Min(max, 99))))
            {
                string code = prefix + next.ToString("00") + new string('0', 6 - prefix.Length) + suffix;
                if (!used.Contains(code[..8])) { orderCode = code; break; }
            }
        }

        return new Suggestion(pid, orderCode, id, id == null ? null : NextIndex(nodes, id));
    }

    private static string NextIndex(List<MenuNode> nodes, string id) =>
        (nodes.Where(n => n.Id == id).Select(n => int.TryParse(n.Index, out var v) ? v : -1).DefaultIfEmpty(-1).Max() + 1).ToString();

    public async Task<MenuNode> CreateMenuAsync(int moduleId, NewMenuItem req, string adminUser, int defaultDays)
    {
        string name = (req.Name ?? "").Trim();
        if (name.Length is 0 or > 100) throw new UserError("Menu name is required (max 100 characters).");
        string orderCode = (req.OrderCode ?? "").Trim().ToUpperInvariant();
        if (!OrderCodeRx().IsMatch(orderCode)) throw new UserError("ORDERCODE must be 8 digits followed by a letter, e.g. 02510000T.");
        string? controller = string.IsNullOrWhiteSpace(req.Controller) ? null : StripSuffix(req.Controller.Trim());
        string action = string.IsNullOrWhiteSpace(req.Action) ? "Index" : req.Action.Trim();
        if (req.IsScreen && (controller == null || !ControllerRx().IsMatch(controller) || !ControllerRx().IsMatch(action)))
            throw new UserError("A screen needs a valid Controller and Action name.");

        await using var conn = await OpenAsync();
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var m = await ModuleAsync(conn, moduleId, tx);
        var nodes = await MenuNodesAsync(conn, m, tx);

        if (await ExistsAsync(conn, tx, $"SELECT COUNT(*) FROM {m.MenuTable} WHERE ORDERCODE = :oc", ("oc", orderCode)))
            throw new UserError($"ORDERCODE {orderCode} is already used in {m.MenuTable}.");

        var sug = Suggest(m, nodes, req.ParentKey);
        string id = string.IsNullOrWhiteSpace(req.Id) ? sug.Id ?? "" : req.Id.Trim().ToUpperInvariant();
        if (!MenuIdRx().IsMatch(id))
            throw new UserError("Menu ID is required when the parent has no children yet (letters/digits/_ , max 11).");
        string index = NextIndex(nodes, id);

        var cols = await ColumnsAsync(conn, null, m.MenuTable, tx);
        string tag = cols.Contains("MENUTAG") ? await ScalarAsync(conn, tx, $"SELECT MAX(MENUTAG) FROM {m.MenuTable} WHERE MODULE = :code", ("code", m.Code)) ?? m.Code : m.Code;
        var values = new List<(string col, object? val)>
        {
            ("ID", id), ("NAME", name), ("INDEX_ORA", index), ("PID", sug.Pid), ("ORDERCODE", orderCode),
            ("PERDOTNETMENU", "Y"), ("ATVDOTNETMENU", "Y"), ("MODULE", m.Code),
            ("IMAGE", req.IsScreen ? null : 2), ("TAG", 0), ("TYPE", req.IsScreen ? "E" : null), ("MENUTAG", tag),
        };
        await InsertAsync(conn, tx, m.MenuTable, values.Where(v => cols.Contains(v.col)).ToList());

        if (req.IsScreen)
            await UpsertDoneAsync(conn, tx, m, orderCode, name, controller!, action, true);

        // Rights for the chosen users (and the headings above the new item).
        nodes = await MenuNodesAsync(conn, m, tx);
        var target = nodes.First(n => n.OrderCode == orderCode);
        foreach (var g in req.GrantTo ?? [])
        {
            var schema = await LinkedSchemaAsync(conn, tx, m, g.User, g.Schema);
            await GrantPathAsync(conn, tx, m, schema, g.User.Trim().ToUpperInvariant(), nodes, target,
                req.IsScreen ? "AEDV" : null, defaultDays, adminUser);
        }

        await tx.CommitAsync();
        Audit.Write(adminUser, $"CREATE menu {m.Code} {orderCode} '{name}' {id}/{index} under {sug.Pid}" +
                               (req.IsScreen ? $" -> {controller}/{action}" : " (heading)") +
                               $"; granted to {string.Join(", ", (req.GrantTo ?? []).Select(g => g.User + "@" + g.Schema))}");
        return target;
    }

    // The item is picked by its ID!INDEX_ORA key: ORDERCODE is not unique in every menu table
    // (e.g. two FIN rows share 09190000F).
    public async Task UpdateMenuAsync(int moduleId, MenuUpdate req, string adminUser)
    {
        await using var conn = await OpenAsync();
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var m = await ModuleAsync(conn, moduleId, tx);
        var nodes = await MenuNodesAsync(conn, m, tx);
        var node = nodes.FirstOrDefault(n => n.Key == req.Key)
                   ?? throw new UserError($"Menu item {req.Key} not found.");
        string orderCode = node.OrderCode;

        var changes = new List<string>();
        string name = string.IsNullOrWhiteSpace(req.Name) ? node.Name : req.Name.Trim();
        if (name.Length > 100) throw new UserError("Menu name max 100 characters.");
        bool active = req.Active ?? node.Active;

        // An item under a switched-off heading stays off: switch the heading on first.
        if (active && !node.Active)
        {
            var byKey = nodes.ToDictionary(n => n.Key);
            var pid = node.Pid;
            for (int guard = 0; byKey.TryGetValue(pid, out var parent) && guard < 20; guard++, pid = parent.Pid)
                if (!parent.Active)
                    throw new UserError($"“{parent.Name}” ({parent.OrderCode}) above this item is switched off - switch it on first.");
        }

        // Switching an item on or off does the same to everything under it.
        if (active != node.Active)
        {
            var under = Descendants(nodes, node).Where(d => d.Active != active).ToList();
            foreach (var d in under)
                await SetActiveAsync(conn, tx, m, nodes, d, active);
            if (under.Count > 0)
                changes.Add($"{(active ? "activated" : "deactivated")} {under.Count} item(s) under it: {string.Join(", ", under.Select(d => d.OrderCode))}");
        }

        await using (var cmd = Cmd(conn, $"UPDATE {m.MenuTable} SET NAME = :n WHERE ID = :id AND INDEX_ORA = :ix AND MODULE = :code", tx))
        {
            cmd.Parameters.Add("n", name);
            cmd.Parameters.Add("id", node.Id);
            cmd.Parameters.Add("ix", node.Index);
            cmd.Parameters.Add("code", m.Code);
            await cmd.ExecuteNonQueryAsync();
        }
        if (active != node.Active) await SetActiveAsync(conn, tx, m, nodes, node, active);
        if (name != node.Name) changes.Add($"name '{node.Name}' -> '{name}'");
        if (active != node.Active) changes.Add(active ? "activated" : "deactivated");

        string? controller = req.Controller == null ? node.Controller : (req.Controller.Trim() == "" ? null : StripSuffix(req.Controller.Trim()));
        string action = string.IsNullOrWhiteSpace(req.Action) ? node.Action ?? "Index" : req.Action.Trim();
        if (controller != null)
        {
            if (!ControllerRx().IsMatch(controller) || !ControllerRx().IsMatch(action))
                throw new UserError("Controller/Action must be plain names.");
            // On/off is handled by SetActiveAsync; here only a new/changed screen link is written.
            if (controller != node.Controller || action != node.Action || name != node.Name)
            {
                bool linkOn = active || nodes.Any(n => n.OrderCode == orderCode && n.Key != node.Key && n.Active);
                await UpsertDoneAsync(conn, tx, m, orderCode, name, controller, action, linkOn);
            }
            if (controller != node.Controller || action != node.Action) changes.Add($"-> {controller}/{action}");
        }

        await tx.CommitAsync();
        Audit.Write(adminUser, $"UPDATE menu {m.Code} {orderCode} ({node.Key}): {(changes.Count == 0 ? "no change" : string.Join("; ", changes))}");
    }

    // Every node below `node` (PID chain), any depth.
    private static List<MenuNode> Descendants(List<MenuNode> nodes, MenuNode node)
    {
        var kids = nodes.ToLookup(n => n.Pid);
        var result = new List<MenuNode>();
        var seen = new HashSet<string> { node.Key };
        var queue = new Queue<MenuNode>([node]);
        while (queue.Count > 0)
            foreach (var c in kids[queue.Dequeue().Key])
                if (seen.Add(c.Key)) { result.Add(c); queue.Enqueue(c); }
        return result;
    }

    // Visible flag of one item: menu row (the ERP menu shows PERDOTNETMENU = 'Y' only) and its screen link.
    private static async Task SetActiveAsync(OracleConnection conn, OracleTransaction tx, ModuleInfo m,
        List<MenuNode> nodes, MenuNode node, bool active)
    {
        await using (var cmd = Cmd(conn, $"UPDATE {m.MenuTable} SET PERDOTNETMENU = :a WHERE ID = :id AND INDEX_ORA = :ix AND MODULE = :code", tx))
        {
            cmd.Parameters.Add("a", active ? "Y" : "N");
            cmd.Parameters.Add("id", node.Id);
            cmd.Parameters.Add("ix", node.Index);
            cmd.Parameters.Add("code", m.Code);
            await cmd.ExecuteNonQueryAsync();
        }
        // VBMENU_DONE is per ORDERCODE: keep the link on while another visible item still uses the same code.
        if (!active && nodes.Any(n => n.OrderCode == node.OrderCode && n.Key != node.Key && n.Active)) return;
        await using var done = Cmd(conn, "UPDATE VBMENU_DONE SET ISACTIVE = :a WHERE MODULECODE = :code AND MENUCODE = :oc", tx);
        done.Parameters.Add("a", active ? 1 : 0);
        done.Parameters.Add("code", m.Code);
        done.Parameters.Add("oc", node.OrderCode);
        await done.ExecuteNonQueryAsync();
    }

    private static async Task UpsertDoneAsync(OracleConnection conn, OracleTransaction tx, ModuleInfo m,
        string orderCode, string name, string controller, string action, bool active)
    {
        await using (var upd = Cmd(conn,
            "UPDATE VBMENU_DONE SET CONTROLLER = :c, ACTIONMETHOD = :a, MENUNAME = :n, ISACTIVE = :act " +
            "WHERE MODULECODE = :code AND MENUCODE = :oc", tx))
        {
            upd.Parameters.Add("c", controller);
            upd.Parameters.Add("a", action);
            upd.Parameters.Add("n", name);
            upd.Parameters.Add("act", active ? 1 : 0);
            upd.Parameters.Add("code", m.Code);
            upd.Parameters.Add("oc", orderCode);
            if (await upd.ExecuteNonQueryAsync() > 0) return;
        }
        // VBMENU_DONE.ID has no sequence - the existing scripts use MAX(ID) + 1 too.
        await using var ins = Cmd(conn,
            "INSERT INTO VBMENU_DONE (ID, MODULECODE, PROJECTNAME, MENUNAME, MENUCODE, CONTROLLER, ACTIONMETHOD, IS4SUBDOMAIN, ISACTIVE) " +
            "SELECT NVL(MAX(ID), 0) + 1, :code, :code, :n, :oc, :c, :a, 1, :act FROM VBMENU_DONE", tx);
        ins.Parameters.Add("code", m.Code);
        ins.Parameters.Add("n", name);
        ins.Parameters.Add("oc", orderCode);
        ins.Parameters.Add("c", controller);
        ins.Parameters.Add("a", action);
        ins.Parameters.Add("act", active ? 1 : 0);
        await ins.ExecuteNonQueryAsync();
    }

    // ---- Users and rights ----------------------------------------------------------------

    public async Task<List<UserLink>> GetUsersAsync(int moduleId)
    {
        await using var conn = await OpenAsync();
        var m = await ModuleAsync(conn, moduleId);
        await using var cmd = Cmd(conn,
            "SELECT UPPER(TRIM(USER_ORA)), UPPER(TRIM(USR_ID)), MAX(LOCA), MAX(MODULE_NAME) FROM USER_SCHEMA_LINK " +
            "WHERE MODULE_CODE = :code AND USER_ORA IS NOT NULL AND USR_ID IS NOT NULL " +
            // only schemas that actually hold this module's rights table
            "  AND EXISTS (SELECT 1 FROM ALL_TABLES t WHERE t.OWNER = UPPER(TRIM(USR_ID)) AND t.TABLE_NAME = :tbl) " +
            "GROUP BY UPPER(TRIM(USER_ORA)), UPPER(TRIM(USR_ID)) ORDER BY 1, 2");
        cmd.Parameters.Add("code", m.Code);
        cmd.Parameters.Add("tbl", m.UsracsTable.ToUpperInvariant());
        var list = new List<UserLink>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(new UserLink(S(r[0])!, S(r[1])!, S(r[2]), S(r[3])));
        return list;
    }

    // The schema must be one the user is actually linked to for this module.
    private static async Task<string> LinkedSchemaAsync(OracleConnection conn, OracleTransaction? tx, ModuleInfo m, string user, string schema)
    {
        string s = Ident(schema, "Schema");
        if (!await ExistsAsync(conn, tx,
                "SELECT COUNT(*) FROM USER_SCHEMA_LINK WHERE MODULE_CODE = :code AND UPPER(TRIM(USER_ORA)) = :u AND UPPER(TRIM(USR_ID)) = :s",
                ("code", m.Code), ("u", (user ?? "").Trim().ToUpperInvariant()), ("s", s)))
            throw new UserError($"{user} is not linked to schema {s} for module {m.Code} (USER_SCHEMA_LINK).");
        return s;
    }

    public async Task<RightsView> GetRightsAsync(int moduleId, string user, string schema)
    {
        await using var conn = await OpenAsync();
        var m = await ModuleAsync(conn, moduleId);
        string s = await LinkedSchemaAsync(conn, null, m, user, schema);
        string u = user.Trim().ToUpperInvariant();
        return new RightsView(await MenuNodesAsync(conn, m), (await RightsAsync(conn, null, m, s, u)).Values.ToList());
    }

    private static async Task<Dictionary<string, RightRow>> RightsAsync(OracleConnection conn, OracleTransaction? tx, ModuleInfo m, string schema, string user)
    {
        var cols = await ColumnsAsync(conn, schema, m.UsracsTable, tx);
        string Col(string c) => cols.Contains(c) ? c : "NULL";
        await using var cmd = Cmd(conn,
            $"SELECT MNUID, INDEX_ORA, MAX(AEDV), MAX({Col("EDAY")}), MAX({Col("ADAY")}), MAX({Col("DDAY")}) " +
            $"FROM {schema}.{m.UsracsTable} WHERE UPPER(TRIM(UID_ORA)) = :u GROUP BY MNUID, INDEX_ORA", tx);
        cmd.Parameters.Add("u", user);
        var map = new Dictionary<string, RightRow>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var row = new RightRow(S(r[0]) ?? "", S(r[1]) ?? "", S(r[2]) ?? "", N(r[3]), N(r[4]), N(r[5]));
            map[row.Id + "!" + row.Index] = row;
        }
        return map;
    }

    // Applies the wanted rights for one user in one schema: a node that is granted (or sits
    // above a granted node) gets a row; a node that is not loses its row. Only nodes of this
    // module's menu are touched.
    public async Task<SaveResult> SaveRightsAsync(int moduleId, RightsSave req, string adminUser, int defaultDays)
    {
        await using var conn = await OpenAsync();
        await using var tx = (OracleTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var m = await ModuleAsync(conn, moduleId, tx);
        string s = await LinkedSchemaAsync(conn, tx, m, req.User, req.Schema);
        string u = req.User.Trim().ToUpperInvariant();
        var nodes = await MenuNodesAsync(conn, m, tx);
        var byKey = nodes.ToDictionary(n => n.Key);
        var existing = await RightsAsync(conn, tx, m, s, u);

        var wanted = new Dictionary<string, (string? aedv, DayLimits days)>();
        foreach (var item in req.Items ?? [])
        {
            string key = item.Id + "!" + item.Index;
            if (!item.Granted || !byKey.ContainsKey(key)) continue;
            string aedv = new string("AEDV".Where(c => (item.Aedv ?? "").ToUpperInvariant().Contains(c)).ToArray());
            // A day value the screen did not send (null) keeps what the row has; a new screen
            // right (no row, or the row had no AEDV) starts at the default.
            existing.TryGetValue(key, out var ex);
            bool hadRights = ex != null && !string.IsNullOrWhiteSpace(ex.Aedv);
            int? Pick(int? req, int? cur) => req is int v ? Math.Clamp(v, 0, 9999) : hadRights ? cur : defaultDays;
            wanted[key] = (aedv == "" ? null : aedv,
                           new DayLimits(Pick(item.Aday ?? item.Days, ex?.Aday), Pick(item.Eday ?? item.Days, ex?.Eday), Pick(item.Dday ?? item.Days, ex?.Dday)));
        }
        // Headings above every granted node.
        foreach (var key in wanted.Keys.ToList())
        {
            var pid = byKey[key].Pid;
            for (int guard = 0; byKey.TryGetValue(pid, out var parent) && guard < 20; guard++, pid = parent.Pid)
                wanted.TryAdd(parent.Key, (null, DayLimits.None));
        }

        int added = 0, changed = 0, removed = 0;
        var log = new List<string>();
        foreach (var n in nodes)
        {
            bool has = existing.TryGetValue(n.Key, out var cur);
            if (wanted.TryGetValue(n.Key, out var w))
            {
                if (!has)
                {
                    await InsertRightAsync(conn, tx, m, s, u, n.Id, n.Index, w.aedv, w.days, adminUser);
                    added++; log.Add($"+{n.OrderCode}:{w.aedv ?? "-"}");
                }
                else if ((cur!.Aedv ?? "") != (w.aedv ?? "") ||
                         (w.aedv != null && ((w.days.Add != null && cur.Aday != w.days.Add) ||
                                             (w.days.Edit != null && cur.Eday != w.days.Edit) ||
                                             (w.days.Delete != null && cur.Dday != w.days.Delete))))
                {
                    await UpdateRightAsync(conn, tx, m, s, u, n.Id, n.Index, w.aedv, w.days);
                    changed++;
                    log.Add($"~{n.OrderCode}:{cur.Aedv}/{cur.Aday},{cur.Eday},{cur.Dday}->{w.aedv ?? "-"}/{w.days.Add},{w.days.Edit},{w.days.Delete}");
                }
            }
            else if (has)
            {
                await using var del = Cmd(conn,
                    $"DELETE FROM {s}.{m.UsracsTable} WHERE UPPER(TRIM(UID_ORA)) = :u AND MNUID = :id AND INDEX_ORA = :ix", tx);
                del.Parameters.Add("u", u); del.Parameters.Add("id", n.Id); del.Parameters.Add("ix", n.Index);
                await del.ExecuteNonQueryAsync();
                removed++; log.Add($"-{n.OrderCode}");
            }
        }

        await tx.CommitAsync();
        if (log.Count > 0) Audit.Write(adminUser, $"RIGHTS {m.Code} {u}@{s}: {string.Join(" ", log)}");
        return new SaveResult(added, changed, removed);
    }

    private static async Task GrantPathAsync(OracleConnection conn, OracleTransaction tx, ModuleInfo m, string schema, string user,
        List<MenuNode> nodes, MenuNode target, string? aedv, int defaultDays, string adminUser)
    {
        var days = new DayLimits(defaultDays, defaultDays, defaultDays);
        var existing = await RightsAsync(conn, tx, m, schema, user);
        var byKey = nodes.ToDictionary(n => n.Key);
        MenuNode? n = target;
        for (int guard = 0; n != null && guard < 20; guard++)
        {
            string? a = n == target ? aedv : null;
            if (!existing.TryGetValue(n.Key, out var cur))
                await InsertRightAsync(conn, tx, m, schema, user, n.Id, n.Index, a, days, adminUser);
            else if (a != null && string.IsNullOrWhiteSpace(cur.Aedv))
                await UpdateRightAsync(conn, tx, m, schema, user, n.Id, n.Index, a, days);
            n = byKey.GetValueOrDefault(n.Pid);
        }
    }

    private static async Task InsertRightAsync(OracleConnection conn, OracleTransaction tx, ModuleInfo m, string schema, string user,
        string id, string index, string? aedv, DayLimits days, string adminUser)
    {
        var cols = await ColumnsAsync(conn, schema, m.UsracsTable, tx);
        var d = aedv == null ? DayLimits.None : days;

        // LOCA / UNIT / SCHEMA_NM as on the user's other rows; else from the company link.
        string? loca = null, unit = null, schemaNm = null;
        await using (var t = Cmd(conn,
            $"SELECT LOCA, {(cols.Contains("UNIT") ? "UNIT" : "NULL")}, {(cols.Contains("SCHEMA_NM") ? "SCHEMA_NM" : "NULL")} " +
            $"FROM {schema}.{m.UsracsTable} WHERE UPPER(TRIM(UID_ORA)) = :u AND ROWNUM = 1", tx))
        {
            t.Parameters.Add("u", user);
            await using var r = await t.ExecuteReaderAsync();
            if (await r.ReadAsync()) { loca = S(r[0]); unit = S(r[1]); schemaNm = S(r[2]); }
        }
        if (loca == null)
        {
            loca = await ScalarAsync(conn, tx,
                "SELECT MAX(LOCA) FROM USER_SCHEMA_LINK WHERE MODULE_CODE = :code AND UPPER(TRIM(USER_ORA)) = :u AND UPPER(TRIM(USR_ID)) = :s",
                ("code", m.Code), ("u", user), ("s", schema));
            schemaNm = schema;
        }

        var values = new List<(string col, object? val)>
        {
            ("LOCA", loca), ("UNIT", unit), ("UID_ORA", user), ("MNUID", id), ("INDEX_ORA", index), ("AEDV", aedv),
            ("ADAY", d.Add ?? 0), ("EDAY", d.Edit ?? 0), ("DDAY", d.Delete ?? 0), ("USER_NAME", adminUser), ("USER_ENTDT", DateTime.Now),
            ("OS_USER", "MENUADMIN"), ("TERMINAL_ID", Environment.MachineName), ("SCHEMA_NM", schemaNm ?? schema),
        };
        await InsertAsync(conn, tx, schema + "." + m.UsracsTable, values.Where(v => cols.Contains(v.col)).ToList());
    }

    private static async Task UpdateRightAsync(OracleConnection conn, OracleTransaction tx, ModuleInfo m, string schema, string user,
        string id, string index, string? aedv, DayLimits days)
    {
        var cols = await ColumnsAsync(conn, schema, m.UsracsTable, tx);
        var d = aedv == null ? DayLimits.None : days;
        var sets = new List<string> { "AEDV = :aedv" };
        // null = leave that column as it is
        var dayCols = new (string col, int? val)[] { ("ADAY", d.Add), ("EDAY", d.Edit), ("DDAY", d.Delete) }
            .Where(c => cols.Contains(c.col) && c.val != null).ToList();
        sets.AddRange(dayCols.Select(c => $"{c.col} = :{c.col.ToLowerInvariant()}"));
        await using var cmd = Cmd(conn,
            $"UPDATE {schema}.{m.UsracsTable} SET {string.Join(", ", sets)} " +
            "WHERE UPPER(TRIM(UID_ORA)) = :u AND MNUID = :id AND INDEX_ORA = :ix", tx);
        cmd.Parameters.Add("aedv", (object?)aedv ?? DBNull.Value);
        foreach (var (col, val) in dayCols) cmd.Parameters.Add(col.ToLowerInvariant(), val);
        cmd.Parameters.Add("u", user); cmd.Parameters.Add("id", id); cmd.Parameters.Add("ix", index);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- small helpers -------------------------------------------------------------------

    private static async Task InsertAsync(OracleConnection conn, OracleTransaction tx, string table, List<(string col, object? val)> values)
    {
        await using var cmd = Cmd(conn,
            $"INSERT INTO {table} ({string.Join(", ", values.Select(v => v.col))}) " +
            $"VALUES ({string.Join(", ", values.Select((_, i) => ":p" + i))})", tx);
        for (int i = 0; i < values.Count; i++)
            cmd.Parameters.Add("p" + i, values[i].val ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ExistsAsync(OracleConnection conn, OracleTransaction? tx, string sql, params (string name, object val)[] ps)
    {
        await using var cmd = Cmd(conn, sql, tx);
        foreach (var (name, val) in ps) cmd.Parameters.Add(name, val);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }

    private static async Task<string?> ScalarAsync(OracleConnection conn, OracleTransaction? tx, string sql, params (string name, object val)[] ps)
    {
        await using var cmd = Cmd(conn, sql, tx);
        foreach (var (name, val) in ps) cmd.Parameters.Add(name, val);
        return S(await cmd.ExecuteScalarAsync() ?? DBNull.Value);
    }
}

public sealed class UserError(string message) : Exception(message);

public record ModuleInfo(int Id, string Name, string Code, string MenuTable, string UsracsTable, string Root, bool Ready = true);

public record MenuNode(string Id, string Index, string Pid, string Name, string? Type, string OrderCode,
                       bool Active, bool AtvActive, string? Controller, string? Action, bool DoneActive)
{
    public string Key => Id + "!" + Index;
}

public record MenuView(ModuleInfo Module, List<MenuNode> Nodes);
public record Suggestion(string Pid, string? OrderCode, string? Id, string? Index);
public record UserLink(string User, string Schema, string? Loca, string? ModuleName);
public record RightRow(string Id, string Index, string Aedv, int? Eday, int? Aday, int? Dday);
public record RightsView(List<MenuNode> Nodes, List<RightRow> Rights);
public record SaveResult(int Added, int Changed, int Removed);

public record GrantTarget(string User, string Schema);
public record NewMenuItem(string? Name, string? ParentKey, string? OrderCode, string? Id,
                          bool IsScreen, string? Controller, string? Action, List<GrantTarget>? GrantTo);
public record MenuUpdate(string Key, string? Name, bool? Active, string? Controller, string? Action);
// ADAY / EDAY / DDAY: how many days back the user may add / edit / delete on the screen.
public record RightItem(string Id, string Index, bool Granted, string? Aedv, int? Days, int? Aday = null, int? Eday = null, int? Dday = null);

public record DayLimits(int? Add, int? Edit, int? Delete)
{
    public static readonly DayLimits None = new(0, 0, 0);
}
public record RightsSave(string User, string Schema, List<RightItem>? Items);

public static class Audit
{
    private static readonly object Gate = new();

    public static void Write(string user, string text)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(dir);
        lock (Gate)
            File.AppendAllText(Path.Combine(dir, $"audit-{DateTime.Now:yyyyMM}.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {user} {text}{Environment.NewLine}");
    }
}
