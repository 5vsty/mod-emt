# EMT + Notifications: handover (November 2026)

For the people who pull, review, build and deploy this change. It covers `MOD/emt`, `VIS/emt`, `MOD/notifications` and `VIS/notifications`.
Branches the work was done on: `ai/emt-notification` (MOD/emt) and `ai/horizon-ticket` (MOD/notifications). The design documents are in the HORIZON
workspace (`FEATURE-2026-001`: BA v0.9, design addendum v0.3, `10-implementation-summary.md`); this file is enough to build and run it.

## 1. What changed

| Area | Change |
|---|---|
| Notifications for the people concerned | An error change creates one notification **per person concerned** (new error → managers except the reporter; assignment → new handler and reporter; status / note / severity → reporter and handler; never the person who acted). A user only sees their own notifications (table, badge, detail, buttons). Details in `README.md` ("Notifications") and `MOD/notifications/README.md`. |
| Click on a notification | A click on the row presses the button `OPEN` (config `ActiveNotifications.RowClickButton`): the page of that user opens the error (managers: full detail, reporters: read only) and the notification is moved to the history. Without `RowClickButton` the buttons `Open` / `OK` are shown as before. |
| Error codes and error numbers from IdManager | New error codes get their code from the IdManager series `EC` (`EC10000001`), new errors their number from the series `ER` (`ER10000001`). The user no longer types a code. Errors created earlier keep `EMT-000001…` and sort before the new ones. If the IdManager does not answer, an error is not created ("unavailable"); nothing half-written stays. |
| Error code catalog follows ILP | EMT waits until its ILP client is synchronised before it reads, and a `CatalogWatcher` reads the catalog again when the states change (the ConfigurationManager can fill them after EMT started). |
| Tables | Every column of the error list, "my reported errors" and the error code table has a filter (dates as a range, the rest as a multiselect). Default filter of the error list: open statuses. |
| Status colours | `StatusColors` in `emtConfig.json` (a tone or own colours per status), default follows the status tags of the UI kit. See `examples/emtConfig.example.json`. |
| Error detail window | Sections like the device manager (`FlexBox`): General, Processing, History (collapsed). `@fsdev/box` is a new dependency of `VIS/emt`. |
| Reporter page | A second tap on a tile of an open error opens that error instead of "already reported"; a resolved error resets the tile (the next tap starts a new round). The headers and the overview placeholder were removed. |
| Notifications module | `RecipientUserId`, filtering by user, `TryRegisterButtons`, `SupportsRecipients` (see its README), row click button in `VIS/notifications`, translations of the plugin, `Utilities.sendCustomTranslations()`. |

## 2. Merge and build order (important)

1. **Merge `MOD/notifications` and `VIS/notifications` first and publish the package `Notifications_Client`.** EMT uses its new API
   (`SupportsRecipients`, `TryRegisterButtons`, `TryCreateNotification` with a recipient, `Notification.RecipientUserId`).
   `Emt_Server.csproj` references `FSDEV.MOD.Notifications.Notifications_Client` with the version `0.0.0`; check how your pipeline pins it and bump it to the new package.
2. Notifications server and EMT must be deployed **together or the server first**. EMT is fail-closed: if the server does not announce
   `Notifications/Capabilities/Recipients` it creates no notification (an old server would show them to everybody) and logs a warning.
3. New dependencies:
   * `MOD/emt`: `FSDEV.MOD.IdManager.IdManager_Client` 3.1.0 (the IdManager server must support free prefixes, like DeviceManager uses them).
   * `VIS/emt`: `@fsdev/box` `1.0.1-implementfeedback-1029` (same as `VIS/device-manager`) → `npm i`.
4. Build: `dotnet build Emt.sln && dotnet test Emt.sln` (145 tests), `dotnet test Notifications_Test/Notifications_Test.csproj` (12 tests),
   `npm run type-check && npm run build` in `VIS/emt` and `VIS/notifications`.

## 3. Configuration (not part of the build output)

* `emtConfig.json`: `Notifications.Enabled: true` and a valid `Notifications.FacilityId`; optionally `StatusColors`. Example: `examples/emtConfig.example.json`.
* `notificationsConfig.json`: `ActiveNotifications.RowClickButton: "OPEN"` and `Columns` without `BUTTONS` (or with it, then without `RowClickButton`);
  `TranslationsPath` must contain the keys `EMT_NOTIF_*` and `EMT_NOTIFICATION_*` of `VIS/emt/src/translation/languages` (forward slashes; the server reads the path only at start).
  Example: `examples/notificationsConfig.example.json` (keep the module key of your site).
* UserManager: `Config/Modules/Notifications/permissions.json` in `PermissionConfigPaths`; `groups.json` needs a group for each scope id of the
  Notifications permissions (and of EMT, scope `b3c5933c-a4c0-4bdc-ba61-518f6041a89b`), otherwise UserManager shows no permissions for the module.
* Users: EMT permissions `Report-Errors` / `Manage-Errors` / `Manage-Catalog` and the Notifications permission to see the table.
* The config in ILP is created by the ConfigurationManager only once. For an existing site, change the values in ILP as well, not only in the json files.
* Notification types `EMT_NOTIF_*` that an older EMT build created (without the `Open` button) must be deleted once; EMT creates them again with the next notification.

## 4. Things that cost time (read before debugging)

| Symptom | Cause |
|---|---|
| Click on a notification does nothing | (a) The action of a button only lives in the memory of the application that owns it; EMT registers them at start (`IlpNotificationPublisher.TryRegisterExistingTypes`), which needs the Notifications client. (b) The Notifications client inside the EMT process reads `defaultModuleConfig.json` from the **root** of the EMT folder; EMT reads its own default from `ModuleConfig/defaultModuleConfig.json`. A copy-deploy that overwrites the root file with the EMT one breaks the client (`Key 'CsvExportConfig' not found`). A normal build puts the right file there (package content of `Notifications`). (c) Pressing the same button on the same notification twice wrote the same text into `ActionQueue` = no ILP change = no callback; fixed in `NotificationTypeHandler` (delete, then create). |
| EMT hangs, no log, assign does nothing, no `LoadModule` for new sessions | An ILP subscription callback runs on the **main thread of the ILP client**, and `SendChangeRequest` waits for that same thread: never write to ILP inside a callback (button actions run in one). `IlpNotificationPublisher.Background` hands the work to the thread pool. The FES client handles all messages on one worker thread, so one blocked handler stops the whole module. |
| Error codes / errors missing from a table | A small number on ILP comes back as an `IntState` (`UsedAt = 0`, `FinalizedAt = 0`); strict readers saw "not there" and skipped the whole entry. Use `IlpNumberReading.TryGetLongTolerant` / `TryGetIntTolerant`. Skipped entries are logged now. |
| Status filter of the list shows rows of other statuses | `Table.defaultFilter.columnName` is the **column name** (`EMT_COL_STATUS`), not the key selector, otherwise a second fixed filter on the same column is sent and the backend query fails. |
| Buttons show a floppy disk | `FlexButton` shows `icon-floppy-o` by default; pass `:icon="false"` (or an `icon-type`). |
| `REJECTED_SERVER_DISCONNECTED`, "missed 3 KeepAlives", EMT stops at start | ILP is overloaded (221 k states) or the machine is out of memory. EMT retries the base states 30 × 10 s. |

## 5. Smoke test after a deployment

1. As an operator: report an error. Managers get "New error …" (not the reporter); the reporter sees "my reported errors" with a status tag.
2. As a manager: click the notification row → the error detail opens (sections General / Processing / History). Assign it to a technician.
3. As the technician: the notification "… was assigned to you" is there; click it → the detail opens; acknowledge → start → resolve with a text.
4. The reporter gets a notification for each step, can open it (read only) and the tile of the code is reset after "resolved"; a tap reports a new error.
5. Error codes page: create a code (code `EC…` is given), filters work on every column; the error list filter "Status" works.
6. Restart EMT; repeat step 2 with an **old** notification (the click must work right after the start).

## 6. Not done / follow-ups

* The history of the Notifications module (`HistoricNotifications`, SQL) does not filter by recipient yet, so notifications with a recipient are not written to it.
* Suggested for the Notifications team: `Notifications_Client` should run button actions off the ILP thread itself, so every module that uses buttons is safe.
* The error number prefix settings `ErrorNumber.Prefix` / `MinDigits` only matter for the old counter now (numbers come from the IdManager).
* The "overview" placeholder block of the error list was removed; the translation keys `EMT_OVERVIEW*` / `EMT_REPORT_HINT` are unused now and can be deleted later.
* Verified on a local runtime with the real ILP, FES, UserManager, IdManager and Notifications; not on a customer environment. The frontend has no unit tests.
* Local build notes (no change in the repo): `IlpParser` 1.2.0 cannot read the project structure of the local ILP; the packages that GitLab provides were replaced by the ones of the local NuGet cache.
