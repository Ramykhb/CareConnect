namespace BabySitting.Services.Messaging;

// Transport implementations and credential validation are added in Phase 5.
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Exchange { get; set; } = "careconnect.events";
    public string Queue { get; set; } = "babysitting.events";
}
