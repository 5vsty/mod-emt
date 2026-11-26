namespace FSDEV.MOD.Emt.Emt;

public static class Constants
{
    public static class Ilp
    {
        public static class ModuleConfig
        {
            public const string ILP_PATH = "Config/Modules/Emt/emtConfig.json";
            public const string DEFAULT_MODULE_CONFIG = "ModuleConfig/defaultModuleConfig.json";
            public const string MODULE_ID = "e7931f2f-80a5-4c2c-9899-6de5df71256e";

            public const string MODULE_VERSION = "ModuleVersion";
            public const string MODULE_INSTANCE_NAME = "ModuleInstanceName";
            public const string ACTIVE = "Active";
            public const string TRANSLATIONS_PATH = "TranslationsPath";

            public const string RETENTION = "Retention";
            public const string CLEANUP_INTERVAL_HOURS = "CleanupIntervalHours";

            public const string LOCATION = "Location";
            public const string LOCATION_ENABLED = "Enabled";
            public const string FDF_VERSION = "FdfVersion";
            public const string PROJECT_PATH = "ProjectPath";
            public const string SELECTABLE_AREA_TYPES = "SelectableAreaTypes";

            public const string ERROR_NUMBER = "ErrorNumber";
            public const string PREFIX = "Prefix";
            public const string MIN_DIGITS = "MinDigits";

            public const string LIST = "List";
            public const string LIVE_UPDATE_DEBOUNCE_MS = "LiveUpdateDebounceMs";

            public const string NOTIFICATIONS = "Notifications";
            public const string NOTIFICATIONS_ENABLED = "Enabled";
            public const string NOTIFICATIONS_FACILITY_ID = "FacilityId";
        }

        public static class Paths
        {
            public const string PERMISSIONS = "Config/Modules/Emt/permissions.json";
            public const string ERROR_CODES = "Config/Modules/Emt/errorCodes.json";
            public const string SETTINGS = "Config/Modules/Emt/settings.json";

            public const string ROOT = "Emt";
            public const string SEQUENCE = "Emt/Sequence";
            public const string ERROR_NUMBER = "Emt/Sequence/ErrorNumber";
            public const string ERRORS = "Emt/Errors";
        }
    }

    public static class Frontend
    {
        public const string ID = "ID";
        public const string FRONTEND_MODULES_GUID = "196f7f4a-a61d-44af-aac6-56659893bfbe";
        public const string EMT_GUID = "33d46b35-cf3a-4ab4-aa07-d3f58cfe5d7f";

        public static class Requests
        {
            public const string GET_REPORT_FORM_DATA = "funcGetReportFormData";
            public const string SUBMIT_ERROR = "funcSubmitError";
            public const string GET_MY_ERRORS = "funcGetMyErrors";
            public const string CANCEL_ERROR = "funcCancelError";
            public const string DELETE_ERROR = "funcDeleteError";
            public const string GET_ERROR_DETAIL = "funcGetErrorDetail";
            public const string CHANGE_ERROR_STATUS = "funcChangeErrorStatus";
            public const string ASSIGN_ERROR = "funcAssignError";
            public const string CHANGE_ERROR_SEVERITY = "funcChangeErrorSeverity";
            public const string ADD_ERROR_NOTE = "funcAddErrorNote";
            public const string GET_ASSIGNABLE_USERS = "funcGetAssignableUsers";
            public const string GET_ERROR_CODES = "funcGetErrorCodes";
            public const string SAVE_ERROR_CODE = "funcSaveErrorCode";
            public const string DELETE_ERROR_CODE = "funcDeleteErrorCode";
            public const string GET_SETTINGS = "funcGetSettings";
            public const string SAVE_SETTINGS = "funcSaveSettings";
        }

        public static class Responses
        {
            public const string GET_REPORT_FORM_DATA = "funcResponseGetReportFormData";
            public const string SUBMIT_ERROR = "funcResponseSubmitError";
            public const string GET_MY_ERRORS = "funcResponseGetMyErrors";
            public const string CHANGE_ERROR = "funcResponseChangeError";
            public const string DELETE_ERROR = "funcResponseDeleteError";
            public const string GET_ERROR_DETAIL = "funcResponseGetErrorDetail";
            public const string GET_ASSIGNABLE_USERS = "funcResponseGetAssignableUsers";
            public const string GET_ERROR_CODES = "funcResponseGetErrorCodes";
            public const string SAVE_ERROR_CODE = "funcResponseSaveErrorCode";
            public const string DELETE_ERROR_CODE = "funcResponseDeleteErrorCode";
            public const string GET_SETTINGS = "funcResponseGetSettings";
            public const string SAVE_SETTINGS = "funcResponseSaveSettings";
        }

        public static class Push
        {
            public const string ERROR_CHANGED = "funcErrorChanged";
            public const string ERROR_CODES_CHANGED = "funcErrorCodesChanged";
        }
    }

    public static class Permissions
    {
        public const string MODULE_ID = "33d46b35-cf3a-4ab4-aa07-d3f58cfe5d7f";
        public const string SCOPE_ID = "b3c5933c-a4c0-4bdc-ba61-518f6041a89b";

        public const string REPORT_ERRORS = "Report-Errors";
        public const string MANAGE_ERRORS = "Manage-Errors";
        public const string MANAGE_CATALOG = "Manage-Catalog";
    }

    public static class Tables
    {
        public const string ERROR_LIST = "997b9483-d628-4135-a81e-5df70cc2caf2";
        public const string ERROR_CODES = "8a8eb2f7-3280-4066-8a4a-676ea5ff6115";
    }

    public static class Catalog
    {
        public const string OTHER_CODE_ID = "41339cce-e950-4bfd-b0dc-5644892ebd71";
        public const string OTHER_CODE = "OTHER";
    }

    public static class Limits
    {
        public const int MAX_DESCRIPTION_LENGTH = 2000;
        public const int MAX_NOTE_LENGTH = 2000;
        public const int MAX_CODE_LENGTH = 50;
        public const int MAX_TITLE_LENGTH = 200;
        public const int MAX_TEXT_FIELD_LENGTH = 2000;
        public const int MAX_GROUP_LENGTH = 100;
        public const int MIN_RETENTION_DAYS = 30;
        public const int DEFAULT_RETENTION_DAYS = 365;
        public const int MAX_MY_ERRORS = 100;
        public const int MY_ERRORS_RECENT_DAYS = 30;
    }

    public static class Service
    {
        public const string APP_NAME = "Emt";
        public const string EVENT_LOG_NAME = "Application";
    }

    public static class Session
    {
        public const string NEW_SESSION_HANDLER_ID = "EmtNewSession";
    }
}
