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

    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(120);


    public WebsiteHeadersHandler(ILogger<WebsiteHeadersHandler> logger)
    {
        _logger = logger;

        var httpClientHandler = new HttpClientHandler
        {
            Proxy = new TorProxyService("127.0.0.1", 9150),
            UseProxy = true
        };
        _torHttpClient = new HttpClient(httpClientHandler);

        _torHttpClient.Timeout = TimeSpan.FromSeconds(130);
    }


    public async Task<bool> HeaderHandlerAsync(string url)
    {
        using var cts = new CancellationTokenSource(_timeout);
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await _torHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (TaskCanceledException)
            {
                _logger.LogCritical($" ===> {url} Timeout of {_timeout.TotalSeconds} seconds elapsed. Returned false.");
                return false;
            }
            catch (HttpRequestException e)
            {
                return HandleHttpRequestException(url, e);
            }
            catch (Exception ex)
            {
                _logger.LogCritical($" ===> {url} Unexpected exception during HTTP request: {ex.Message}");
                return false;
            }


            if (response.StatusCode == HttpStatusCode.Redirect || response.StatusCode == HttpStatusCode.MovedPermanently)
            {
                var redirectedUri = response.Headers.Location?.ToString();
                if (redirectedUri != null && redirectedUri.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogCritical($"Redirected to disabled page: from {url} === to === > {redirectedUri}");
                    return false;
                }
            }

            var statusCode = (int)response.StatusCode;

            if (statusCode == 522)
            {
                _logger.LogWarning($"==== {url} with status code {response.StatusCode}. Returned false");
                return false;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                string htmlContent;
                try
                {
                    htmlContent = await ReadContentWithTimeoutAsync(response, cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error reading content for {url} with status {response.StatusCode}: {ex.Message}");
                    return false;
                }

                try
                {
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
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error parsing HTML content for {url}: {ex.Message}");
                    return true;
                }
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


            string content;
            try
            {
                content = await ReadContentWithTimeoutAsync(response, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error reading content for {url}: {ex.Message}");
                return true;
            }

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
            _logger.LogError($" ===> {url} TaskCanceledException. Timeout elapsed.");
            return false;
        }
        catch (HttpRequestException e)
        {
            return HandleHttpRequestException(url, e);
        }
        catch (Exception ex)
        {
            _logger.LogCritical($" ===> {url} Unexpected exception: {ex.Message}");
            if (ex.InnerException != null)
            {
                _logger.LogError($"Inner Exception: {ex.InnerException.Message}");
            }
            return false;
        }
    }


    private async Task<string> ReadContentWithTimeoutAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error reading content: {ex.Message}");
            return string.Empty;
        }
    }


    private bool HandleHttpRequestException(string url, HttpRequestException e)
    {
        if (e.Message.Contains("The SSL connection could not be established"))
        {
            if (e.InnerException != null && e.InnerException.Message.Contains("TLS alert: '112'"))
            {
                _logger.LogError($"LogError IN CATCH ===>>> {url} SSL TLS alert 112 error: {e.Message}");
                return false;
            }

            _logger.LogError($"LogError IN CATCH ===>>> {url} Other SSL error: {e.Message}\nInnerException: {e.InnerException}");
            return true;
        }
        if (e.Message.Contains("The request was aborted."))
        {
            if (e.InnerException != null && e.InnerException.Message.Contains("SOCKS server failed to connect to the destination"))
            {
                _logger.LogError($"Returned false. Inner Exception ===>>> {url} Request aborted: {e.InnerException}");
                return false;
            }
            _logger.LogError($"LogError IN CATCH ===>>> {url} Request aborted: {e.Message}");
            _logger.LogError($"Inner Exception===>>> {url} Request aborted: {e.InnerException}");

            return true;
        }

        _logger.LogCritical($" ===> {url} HttpRequestException: {e.Message}");
        if (e.InnerException != null)
        {
            _logger.LogError("Inner Exception:{InnerException}: ", e.InnerException.Message);
        }
        return false;
    }


    private bool IsParkingPage(string content)
    {
        try
        {
            string[] parkingIndicators =
            {
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
        catch (Exception ex)
        {
            _logger.LogError($"Error checking parking page: {ex.Message}");
            return false;
        }
    }


    private bool IsErrorPage(string content)
    {
        try
        {
            string[] errorIndicators =
            {
                "404 error",
                // "page not found",
                "this page can't be found", 
                "http error 404", 
                "no webpage was found", 
                "HTTP ERROR 404",
                // "Not Found",
                "HTTP 404", 
                "This site can't be reached",
                "This page could not be found", 
                "404 Not Found", 
                "HTTP Status 400 – Bad Request",
                "not found on this server",
                // "no longer available"
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
        catch (Exception ex)
        {
            _logger.LogError($"Error checking error page: {ex.Message}");
            return false;
        }
    }
}