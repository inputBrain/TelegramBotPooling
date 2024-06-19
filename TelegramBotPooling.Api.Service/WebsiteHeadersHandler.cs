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
            var startTime = DateTime.Now;

            using var httpClientHandler = new HttpClientHandler();

            httpClientHandler.Proxy = new TorProxyService("127.0.0.1", 9150);
            httpClientHandler.UseProxy = true;

            using var torHttpClient = new HttpClient(httpClientHandler);
            torHttpClient.Timeout = TimeSpan.FromSeconds(150);

            var cts = new CancellationTokenSource();
            var responseTask = torHttpClient.GetAsync(url, cts.Token);


            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120), cts.Token);
            var completedTask = await Task.WhenAny(responseTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                cts.Cancel();
                _logger.LogCritical($"\n\n Site -------> {url} Timeout of 120 seconds elapsing. Returned false \n\n");
                return false;
            }

            var response = await responseTask;

            _logger.LogInformation($"\n\n Response time: {(DateTime.Now - startTime).TotalSeconds} seconds ");


            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return true;
            }

            if (response.Content.Headers.ContentLength == 0 && response.StatusCode != HttpStatusCode.RedirectMethod)
            {
                return false;
            }

            if (response.Headers.TryGetValues("Connection", out var connectionValues))
            {
                var enumerable = connectionValues as string[] ?? connectionValues.ToArray();
                if (enumerable.Contains("keep-alive"))
                {
                    return true;
                }
                if (enumerable.Contains("close"))
                {
                    _logger.LogCritical("Site: {Url} | Connection : close", url);
                    return false;
                }
            }

            return false;
        }
        catch (TaskCanceledException)
        {
            _logger.LogCritical($"\n\n Site -------> {url} catch TaskCanceledException. Timeout of 120 seconds elapsing\n\n");
            return false;
        }
        catch (HttpRequestException e)
        {
            _logger.LogCritical($"\n\n Site -------> {url} catch HttpRequestException. {e.Message}\n\n");

            if (e.Message.Contains("The SSL connection could not be established, see inner exception."))
            {
                return true;
            }
            return false;

        }
    }
}