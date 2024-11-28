using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TelegramBotPooling.Api.Service;

public class WebsiteHeadersHandler : IWebsiteHeadersHandler
{
    private readonly ILogger<WebsiteHeadersHandler> _logger;
    private readonly HttpClient _client;
    
    private readonly HttpClient _torHttpClient;

    public WebsiteHeadersHandler(ILogger<WebsiteHeadersHandler> logger, HttpClient client)
    {
        _logger = logger;
        _client = client;

        var httpClientHandler = new HttpClientHandler
        {
            Proxy = new TorProxyService("127.0.0.1", 9150),
            UseProxy = true
        };
        _torHttpClient = new HttpClient(httpClientHandler);
        _torHttpClient.Timeout = TimeSpan.FromSeconds(150);
    }

    public async Task<bool> HeaderHandlerAsync(string url)
    {
        try
        {
            var cts = new CancellationTokenSource();
            var responseTask = _torHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(140), cts.Token);
            var completedTask = await Task.WhenAny(responseTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                cts.Cancel();
                _logger.LogCritical($" ===> {url} Timeout of 140 seconds elapsing. Returned false.");
                return false;
            }

            var response = await responseTask;

            // if (response.Headers.Location != null)
            // {
            //     var locationUrl = response.Headers.Location.ToString();
            //
            //     _logger.LogWarning($"Redirect detected to {locationUrl}");
            //
            //
            //     if (locationUrl.Contains("lander", StringComparison.OrdinalIgnoreCase))
            //     {
            //         _logger.LogCritical($"Site: {url} redirected to {locationUrl}, identified as a parking page.");
            //         return false;
            //     }
            // }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return true;
            }
            if (response.Content.Headers.ContentLength == 0 && response.StatusCode != HttpStatusCode.RedirectMethod)
            {
                _logger.LogWarning($" --- Possible white page. I will check it again \nSite: {url} ");
                return true;
            }

            var finalUri = response.RequestMessage?.RequestUri?.ToString();
            

            if (!string.IsNullOrEmpty(finalUri))
            {
                var uri = new Uri(finalUri);
                var path = uri.AbsolutePath.TrimEnd('/'); 
                
                if (path.Equals("/leader", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogCritical($"Final URL {finalUri} ends with '/leader', identified as a parking page.");
                    return false;
                }
            }


            var content = await response.Content.ReadAsStringAsync(cts.Token);
            if (IsParkingPage(content))
            {
                _logger.LogCritical($"Site: {url} identified as a parking page based on content.");
                return false;
            }

            return true;
        }
        catch (TaskCanceledException)
        {
            _logger.LogError($" ===> {url} TaskCanceledException. Timeout elapsing.");
            return false;
        }
        catch (HttpRequestException e)
        {
            if (e.Message.Contains("The SSL connection could not be established") || e.Message.Contains("The request was aborted."))
            {
                _logger.LogDebug($"DEBUG ===>>> {url} HttpRequestException: {e.Message}");
                return true;
            }

            _logger.LogCritical($" ===> {url} HttpRequestException: {e.Message}");
            if (e.InnerException != null)
            {
                _logger.LogError("Inner Exception:{InnerException}: ",  e.InnerException.Message);
            }
            return false;
        }
    }


    private bool IsParkingPage(string content)
    {
        string[] parkingIndicators = {
            "domain is for sale",
            "this domain is parked",
            "buy this domain",
            "domain name for sale",
            "find available domain names",
            "advertiser links",
            "top searches",
            "learn more about this domain",
            "the domain owner has not yet uploaded a website"
        };

        foreach (var indicator in parkingIndicators)
        {
            if (content.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Parking indicator: {0}", indicator);
                return true;
            }
        }

        return false;
    }
}