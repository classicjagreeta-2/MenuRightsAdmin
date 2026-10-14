# Menu & Rights Admin

Standalone web app for maintaining ERP menus and user rights without hand-written SQL.

* **Menus** - pick a module, see its menu tree, add a heading or a screen anywhere in it,
  rename, change the controller/action, activate / deactivate an item.
* **User permissions** - pick a user @ company schema, tick which menu items they get and
  their A(dd) / E(dit) / D(elete) / V(iew) rights per screen.

## Run

```
dotnet run --project D:\GIT\MenuRightsAdmin --launch-profile MenuRightsAdmin
```

Opens on http://localhost:5180. Needs .NET 8.

## Configure (`appsettings.json`)

| Key | Meaning |
|---|---|
| `ConnectionStrings:ClassicNew` | Oracle login of the ClassicNew (main) schema. Put real values in `appsettings.Development.json` (git-ignored) or an environment variable `ConnectionStrings__ClassicNew`. |
| `Auth:LoginUrl` | ClassicERPCoreAPI `/Users/authenticate` - credentials are checked there, exactly like the ERP login. |
| `Auth:AcceptAnyServerCertificate` | `true` only for a local API with a self-signed certificate. |
| `Admin:Users` | User IDs allowed to sign in. Re-read on every request - remove a name and that user is locked out immediately. |
| `Defaults:GrantUsers` | Users pre-ticked under "Give access to" when creating an item. |
| `Defaults:Days` | EDAY / ADAY / DDAY for granted screens (365). |

## What it writes

| Action | Tables |
|---|---|
| New item | `<MODULE.MENU_TABLE>` row (ID = siblings' ID, INDEX_ORA = next free, PID = parent `ID!INDEX_ORA`); for screens a `VBMENU_DONE` row (MAX(ID)+1, IS4SUBDOMAIN 1); rights rows for the chosen users on the item and every heading above it |
| Active switch | `<MENU_TABLE>.PERDOTNETMENU` Y/N (the ERP menu shows only `Y`), `VBMENU_DONE.ISACTIVE` |
| Permissions | `<schema>.<MODULE.USRACS_TABLE>`: insert / update `AEDV`, `EDAY/ADAY/DDAY` / delete (untick = row removed = item hidden). Headings above a granted screen are granted automatically. |

ORDERCODE is suggested (parent's code + next free 2-digit pair at the next level) and can be
overwritten. Every change is appended to `bin/.../logs/audit-YYYYMM.log` with the admin's name.

The menu tables and `VBMENU_DONE` are shared with production apps - a deactivated item
disappears for every user of that module.
