using System;
using System.Net;

namespace TelegramBotPooling.Api.Service;

public class TorProxyService : IWebProxy
{
    private readonly Uri _proxyUri;
    public ICredentials Credentials { get; set; } = null!;


    public TorProxyService(string proxyHost, int proxyPort)
    {
        _proxyUri = new Uri($"socks5://{proxyHost}:{proxyPort}");
    }


    public Uri GetProxy(Uri destination)
    {
        return _proxyUri;
    }

    public bool IsBypassed(Uri host)
    {
        return false;
    }
}