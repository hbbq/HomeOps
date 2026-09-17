namespace HomeOps.Api.SmartThingsAuth;

public sealed class SmartThingsAuthorizationRequiredException(string message) : InvalidOperationException(message);
