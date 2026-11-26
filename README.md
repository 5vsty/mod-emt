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
| `ErrorNumber.Prefix` / `MinDigits` | `EMT-` / `6` |
| `List.LiveUpdateDebounceMs` | `1000` |

## Notifications

With `Notifications.Enabled: true` and a valid `Notifications.FacilityId` (a facility group of the Notifications module) in `emtConfig.json`, every new error creates one notification (`Notifications_Client`, application name `Emt`):

| Item | Value |
|---|---|
| Notification types | `EMT_ERROR_INFO`, `EMT_ERROR_WARNING`, `EMT_ERROR_ERROR`, `EMT_ERROR_CRITICAL` (severity level `INFO`, `WARN`, `ERR`, `ERR`), created on first use; message key `EMT_NOTIFICATION_NEW_ERROR` |
| Custom fields | `ErrorNumber`, `ErrorCode`, `CodeTitle` (+ `_de`, `_en`), `LocationPath`, `ReporterName`, `Severity` |
| Referenced id | the error id |
| Archive | when the error is resolved, closed, rejected or cancelled; re-opening a resolved error creates no new notification |

The work runs in the background in the order of the changes (`NotificationObserver`), so a slow or missing Notifications module never delays a report. The result is kept on the error: `NotificationId` and `NotificationState` (`None`, `Active`, `Archived`, `Failed`). EMT starts and works without the Notifications module. The Notifications module shows a notification to everybody; limiting it to the error managers is a task of that module.

Note: the config in ILP is created by ConfigurationManager only once; to switch the notifications on for an existing site the `Notifications` values must be changed in ILP as well, not only in the json file.

## Known limitations

- Notifications are optional and off by default, see "Notifications" below.
- The facility structure is read with the IlpParser, which opens its own ILP connection for every read. The locations are cached for 10 minutes.
- Package versions are the ones available in the local NuGet cache (`FSDEV.FES.Server.Client` 3.7.0, `CSharpControls.Table` 7.2.0, `SystemState_Client` 4.1.0, `Common.Logger` 2.3.0, `UserManager_Client` 6.0.0, `IlpParser` 1.2.0), not the set of `MOD/notifications@develop`.
- The ChangeRequests against a real ILP were only checked against the ILP library in unit tests, not against a running ILP server.

## Build and test

```
dotnet build Emt.sln
dotnet test Emt.sln
```

There is no `nuget.config` in this folder; restore uses the NuGet configuration of the machine.
