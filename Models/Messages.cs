namespace AriaUI.Models;

public record TasksUpdatedMessage();

public record GlobalStatUpdatedMessage(AriaGlobalStat Stat, bool IsConnected);

public record NotificationMessage(string Message, bool IsError = false);

public record ThemeChangedMessage(string ThemeMode);
