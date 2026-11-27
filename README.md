# Emt

EMT (Error Messaging Tool) is the backend module for reporting errors that are not conveyor errors (hardware outside the conveyor, anything else).
It owns its own error code catalog, a seven-status error lifecycle with a history, and three permissions. Created from `MOD/PluginBase`.
Design: FEATURE-2026-001 (05-technical-design, 07-api-and-data-contracts in the HORIZON workspace).

## Where the data lives

Everything is stored on ILP, nothing on disk and no database (D-11: no archive).

| ILP path | Owner | Content |
|---|---|---|
| `Config/Modules/Emt/emtConfig.json` | project seed | ModuleConfig, key `e7931f2f-80a5-4c2c-9899-6de5df71256e` |
| `Config/Modules/Emt/permissions.json` | project seed, members written by UserManager | permissions `Report-Errors`, `Manage-Errors`, `Manage-Catalog` in scope `b3c5933c-a4c0-4bdc-ba61-518f6041a89b`, module id `33d46b35-cf3a-4ab4-aa07-d3f58cfe5d7f` |
| `Config/Modules/Emt/errorCodes.json` | EMT | error code catalog (created on first start, with the fixed "other" code) |
| `Config/Modules/Emt/settings.json` | EMT | `RetentionDays` (default 365, at least 30), `OtherCodeEnabled`, `Version` |
| `Emt/Sequence/ErrorNumber` | EMT | last error number that was given out |
| `Emt/Errors/<yyyyMM>/<ErrorId>` | EMT | one dictionary per error, month of the report (UTC) |

Only EMT writes the keys `Emt/*` and `Config/Modules/Emt/{errorCodes,settings}.json`. Run exactly one instance per site (ILP has no compare-and-set).
Errors that reached a final status (`CLOSED`, `REJECTED`, `CANCELLED`) are removed by the retention job after `RetentionDays`.
Errors that are not final, including `RESOLVED`, are never removed. Add `Config/Modules/Emt/permissions.json` to `PermissionConfigPaths` of UserManager.

## ModuleConfig

`src/Emt/ModuleConfig/defaultModuleConfig.json` holds the defaults; `ModuleVersion` and `ModuleInstanceName` have to come from ILP.
The module is **inactive by default** (`Active: false`): it then only sends `LoadModule` to the frontend. The ModuleConfig is sent to every frontend session.

| Field | Default |
|---|---|
| `Active` | `false` |
| `TranslationsPath` | `""` (table headers are not translated) |
| `Retention.CleanupIntervalHours` | `24` |
| `Location.FdfVersion` / `ProjectPath` / `SelectableAreaTypes` | `1` / `Config/project.json` / `[]` (all area types) |
| `ErrorNumber.Prefix` / `MinDigits` | `EMT-` / `6` (only the counter of the errors of earlier versions; new numbers come from the IdManager, series `ER`) |
| `List.LiveUpdateDebounceMs` | `1000` |
| `StatusColors` | one entry per status: a tone (`neutral`, `info`, `success`, `warning`, `error`) or `{ "Background": "#...", "Text": "#..." }`; sent to the frontend, a missing status keeps its default (NEW error, ACKNOWLEDGED warning, IN_PROGRESS info, RESOLVED / CLOSED success, REJECTED / CANCELLED neutral) |

## Notifications

With `Notifications.Enabled: true` and a valid `Notifications.FacilityId` (a facility group of the Notifications module) in `emtConfig.json`, every change of an error creates one notification **for each person concerned** (`Notifications_Client`, application name `Emt`). A person only sees the own notifications (the Notifications module filters by recipient).

| Change | Who is told (never the person who made the change) |
|---|---|
| New error | every user with Manage-Errors, except the reporter |
| Assigned / handler changed | the new handler ("assigned to you"), the reporter ("handler is ...") |
| Status, note, severity | the reporter and the handler |
| Reporter cancels an error nobody works on | every user with Manage-Errors |

A recipient must still have the permission (reporter: Report-Errors or Manage-Errors; handler/manager: Manage-Errors).

| Item | Value |
|---|---|
| Notification types | `EMT_NOTIF_NEW_ERROR_<SEVERITY>` (icon of the severity), `EMT_NOTIF_ASSIGNED`, `EMT_NOTIF_ASSIGNEE_CHANGED`, `EMT_NOTIF_STATUS_CHANGED`, `EMT_NOTIF_NOTE_ADDED`, `EMT_NOTIF_SEVERITY_CHANGED`, created on first use; message key = type without the severity; texts use `$_prop{Name}` placeholders |
| Custom fields | `ErrorNumber`, `ErrorCode`, `CodeTitle` (+ `_de`, `_en`), `LocationPath`, `ActorName`, `Detail` (+ `_de`, `_en`), `Severity` |
| Referenced id | the error id |
| Buttons | "Open" opens the error in EMT for the person who pressed it; "OK" moves the notification of that person to the history |
| Archive | all earlier notifications of an error when it is closed, rejected or cancelled (the notifications of that change stay until "OK"); a resolved error keeps them |

The work runs in the background in the order of the changes (`NotificationObserver`), so a slow or missing Notifications module never delays a report. The result is kept on the error: `NotificationId` (the ids of the active notifications, separated by `;`) and `NotificationState` (`None`, `Active`, `Archived`, `Failed`). EMT starts and works without the Notifications module.

**Fail-closed:** EMT only creates notifications if the Notifications server says it knows recipients (ILP `Notifications/Capabilities/Recipients`). An older Notifications module would show them to everybody, so EMT creates none and logs a warning.

Site setup of the Notifications module: the translations of EMT (`VIS/emt/src/translation/languages`) must be in its `TranslationsPath`; the three scopes of its permissions need a group each in `groups.json`; either set `ActiveNotifications.RowClickButton: "OPEN"` (a click on the row opens the error) or put `BUTTONS` into `ActiveNotifications.Columns` to show the buttons.

Note: the config in ILP is created by ConfigurationManager only once; to switch the notifications on for an existing site the `Notifications` values must be changed in ILP as well, not only in the json file.

## Error codes and error numbers

The code of a new error code (`EC10000001`) and the number of a new error (`ER10000001`) come from the IdManager (`IdManager_Client`, series `EC` and `ER`, registered on first use). Without an answer of the IdManager nothing is created. Errors of earlier versions keep `EMT-000001…`.
Every numeric field that EMT reads from ILP is read tolerant (`IlpNumberReading`): a long with a small value comes back as an int.

## Handover

See [docs/HANDOVER-2026-11.md](docs/HANDOVER-2026-11.md) (merge order, dependencies, configuration, pitfalls, smoke test) and `docs/examples/`.

## Known limitations

- Notifications are optional and off by default, see "Notifications" above.
- The facility structure is read with the IlpParser, which opens its own ILP connection for every read. The locations are cached for 10 minutes.
- Package versions are the ones available in the local NuGet cache (`FSDEV.FES.Server.Client` 3.7.0, `CSharpControls.Table` 7.2.0, `SystemState_Client` 4.1.0, `Common.Logger` 2.3.0, `UserManager_Client` 6.0.0, `IlpParser` 1.2.0), not the set of `MOD/notifications@develop`.
- The ChangeRequests against a real ILP were only checked against the ILP library in unit tests, not against a running ILP server.

## Build and test

```
dotnet build Emt.sln
dotnet test Emt.sln
```

There is no `nuget.config` in this folder; restore uses the NuGet configuration of the machine.
