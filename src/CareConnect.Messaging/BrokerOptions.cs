namespace CareConnect.Messaging;

public sealed class BrokerOptions
{
    public bool Enabled { get; set; }
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public bool UseTls { get; set; }
    public string Exchange { get; set; } = "careconnect.events";
    public string Queue { get; set; } = "";
    public string Platform { get; set; } = "";
}
