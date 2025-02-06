using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace TelegramBotPooling.Api.Service;

public class WebsiteHeadersHandler : IWebsiteHeadersHandler
{
    private readonly ILogger<WebsiteHeadersHandler> _logger;
    private readonly HttpClient _torHttpClient;

    public WebsiteHeadersHandler(ILogger<WebsiteHeadersHandler> logger)
    {
        _logger = logger;

        var httpClientHandler = new HttpClientHandler
        {
            Proxy = new TorProxyService("127.0.0.1", 9150),
            UseProxy = true
        };
        _torHttpClient = new HttpClient(httpClientHandler);
        _torHttpClient.Timeout = TimeSpan.FromSeconds(120);
    }

    public async Task<bool> HeaderHandlerAsync(string url)
    {
        try
        {
            var uriToCheck = new Uri(url);
            try
            {
                await Dns.GetHostEntryAsync(uriToCheck.Host);
            }
            catch (SocketException)
            {
                _logger.LogError($"DNS resolution failed for {uriToCheck.Host}. Url: {url}. Host might be unreachable.");
                return false;
            }
            
            
            using var ctsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var responseTask = _torHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ctsTimeout.Token);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120), ctsTimeout.Token);
            var completedTask = await Task.WhenAny(responseTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _logger.LogCritical($" ===> {url} Timeout of 120 seconds elapsing. Returned false.");
                return false;
            }

            var response = await responseTask;
            
            if (response.StatusCode == HttpStatusCode.Redirect || response.StatusCode == HttpStatusCode.MovedPermanently)
            {
                var redirectedUri = response.Headers.Location?.ToString();
                if (redirectedUri != null && redirectedUri.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogCritical($"Redirected to disabled page: from {url} === to === > {redirectedUri}");
                    return false;
                }
            }
            
            // if (url == "https://lgamifeed.com/VXFVJ")
            // {
            //     var htmlContent = await response.Content.ReadAsStringAsync();
            //     var htmlDoc = new HtmlDocument();
            //     htmlDoc.LoadHtml(htmlContent);
            //     
            //     _logger.LogWarning($"Detected '403 Forbidden' in <h1> and 'nginx' in structure. Returning false.\n" +
            //                        $" {url}\nCONTENT: {response.Content}\n" +
            //                        $"Status code: {response.StatusCode}\n" +
            //                        $"Is success: {response.IsSuccessStatusCode}\n" +
            //                        $"ReasonPhrase: {response.ReasonPhrase}\n" +
            //                        $"INNER TEXT: {htmlDoc.DocumentNode.InnerText ?? "inner text is null"}");
            //     return false;
            // }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var htmlContent = await response.Content.ReadAsStringAsync();
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(htmlContent);
                
                var h1Node = htmlDoc.DocumentNode.SelectSingleNode("//h1");

                if (h1Node != null && h1Node.InnerText.Trim().Equals("403 Forbidden", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                
                var hrNode = htmlDoc.DocumentNode.SelectSingleNode("//hr");
                
                if (hrNode == null)
                {
                    return true;
                }

                var nginxNode = hrNode.SelectSingleNode("following-sibling::center[text()='nginx']");
                if (nginxNode == null)
                {
                    return true; 
                }

                _logger.LogWarning($"Detected '403 Forbidden' in <h1> and 'nginx' in structure. Returning false.\n {url}\nCONTENT: {htmlContent}\n\n");
                return false;
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


            var content = await response.Content.ReadAsStringAsync();
            if (IsParkingPage(content))
            {
                _logger.LogCritical($"Site: {url} identified as a parking page based on content.");
                return false;
            }
            
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (!string.IsNullOrWhiteSpace(content))
                {
                    if (IsErrorPage(content))
                    {
                        _logger.LogWarning($" ===> {url} returned 404 with error-like content.");
                        return false;
                    }
            
                    _logger.LogWarning($" ===> {url} returned 404, but appears to have meaningful content.");
                    return true;
                }
                _logger.LogWarning($" ===> {url} returned 404 with no content. CONTENT: {content}\n");
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
                _logger.LogError($"LogError IN CATCH ===>>> {url} HttpRequestException: {e.Message}");
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
                _logger.LogError("====>>>> Parking indicator: {0}", indicator);
                return true;
            }
        }

        return false;
    }
    
    
    private bool IsErrorPage(string content)
    {
        string[] errorIndicators = {
            "404 error",
            // "page not found",
            "this page can't be found",
            "http error 404",
            "no webpage was found",
            "HTTP ERROR 404",
            // "Not Found",
            "HTTP 404",
            "This site can’t be reached",
            "This page could not be found",
            "404 Not Found",
            "HTTP Status 400 – Bad Request",
            "not found on this server",
        };

        foreach (var indicator in errorIndicators)
        {
            if (content.Contains(indicator, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError($" ====>>>> Error indicator detected: {indicator}");
                return true;
            }
        }
        return false;
    }
}