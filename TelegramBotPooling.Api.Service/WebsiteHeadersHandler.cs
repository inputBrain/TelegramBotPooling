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


    public WebsiteHeadersHandler(ILogger<WebsiteHeadersHandler> logger, HttpClient client)
    {
        _logger = logger;
        _client = client;
    }


    public async Task<bool> HeaderHandlerAsync(string url)
    {
        try
        {
            using var httpClientHandler = new HttpClientHandler();
            httpClientHandler.Proxy = new TorProxyService("127.0.0.1", 9150);
            httpClientHandler.UseProxy = true;

            using var torHttpClient = new HttpClient(httpClientHandler);
            torHttpClient.Timeout = TimeSpan.FromSeconds(60);

            var cts = new CancellationTokenSource();
            var responseTask = torHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60), cts.Token);
            var completedTask = await Task.WhenAny(responseTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                cts.Cancel();
                _logger.LogCritical($"Site -------> {url} Timeout of 60 seconds elapsing. Returned false.");
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
                _logger.LogCritical($"Returned FALSE in if(response.Content.Headers.ContentLength == 0 && response.StatusCode != HttpStatusCode.RedirectMethod) \nSite: {url} ");
                return false;
            }

            var finalUri = response.RequestMessage?.RequestUri?.ToString();
            

            if (!string.IsNullOrEmpty(finalUri))
            {
                var uri = new Uri(finalUri);
                // var path = uri.AbsolutePath;
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
            _logger.LogError($"Site -------> {url} TaskCanceledException. Timeout elapsing.");
            return false;
        }
        catch (HttpRequestException e)
        {
            _logger.LogWarning($"Site -------> {url} HttpRequestException: {e.Message}");

            if (e.Message.Contains("The SSL connection could not be established"))
            {
                return true;
            }

            return false;
        }
    }


    private bool IsParkingPage(string content)
    {
        string[] parkingIndicators = {
            "domain is for sale",
            "related searches",
            "this domain is parked",
            "the domain is parked",
            "buy this domain",
            "domain name for sale",
            "click here to make an offer",
            "this domain may be for sale",
            "under construction",
            "this site is under construction",
            "find available domain names",
            "ads by google",
            "sponsored listings",
            "sponsored results",
            "advertiser links",
            "search results for",
            "top searches",
            "click here to continue",
            "learn more about this domain",
            "contact us for more information",
            "the domain owner has not yet uploaded a website"
        };

        foreach (var indicator in parkingIndicators)
        {
            if (content.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}