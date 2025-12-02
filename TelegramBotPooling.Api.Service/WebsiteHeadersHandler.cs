using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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

            if (statusCode == 526)
            {
                _logger.LogWarning($"==== {url} with status code {response.StatusCode}. Returned false");
                return false;
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
            
            if (IsErrorPageByTitle(content))
            {
                _logger.LogWarning($" <----temp_effective_check----> {url} has error indicator in page title.");
                // return false;
            }
            
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                if (IsStrictNginx404Page(content))
                {
                    _logger.LogWarning($"Detected strict nginx 404 page structure for {url}. Returning false.");
                    return false;
                }
                
                if (!string.IsNullOrWhiteSpace(content))
                {
                    if (IsErrorPage(content))
                    {
                        _logger.LogWarning($" ===> {url} returned 404 with error-like content.");
                        return false;
                    }
                    
                    // if (IsErrorPageByTitle(content))
                    // {
                    //     _logger.LogWarning($" ===> {url} has error indicator in page title. not returned any value");
                    //     // return false;
                    // }

                    _logger.LogWarning($" ===> {url} returned 404, but appears to have meaningful content.");
                    return true;
                }

                _logger.LogWarning($" ===> {url} returned 404 with no content. CONTENT: {content}");
                return false;
            }
            
            if (response.Content.Headers.ContentLength == 0 && response.StatusCode != HttpStatusCode.RedirectMethod)
            {
                _logger.LogWarning($" --- Possible white page. I will check it again \nSite: {url} ");
                
                if (await IsWhitePageAsync(response, cts.Token))
                {
                    _logger.LogWarning($"Site {url} detected as white page (no visible text in first 16 KB)");
                    // return false;
                }
                
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
    
    
    private async Task<bool> IsWhitePageAsync(HttpResponseMessage response, CancellationToken token)
    {
        const int maxBytes = 16 * 1024;
        Stream stream;

        try
        {
            stream = await response.Content.ReadAsStreamAsync(token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("ReadAsStreamAsync was canceled or timed out.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to get response stream: {ex.Message}");
            return false;
        }

        await using (stream)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            var totalRead = 0;
            
            try
            {
                while (totalRead < maxBytes)
                {
                    int toRead = Math.Min(buffer.Length, maxBytes - totalRead);
                    int read = await stream.ReadAsync(buffer, 0, toRead, token);
                    if (read == 0)
                        break;

                    await ms.WriteAsync(buffer, 0, read, token);
                    totalRead += read;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Reading from the stream was canceled or timed out.");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error while reading from the stream: {ex.Message}");
                return false;
            }

            string snippet;
            try
            {
                snippet = Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to decode the first {totalRead} bytes: {ex.Message}");
                return false;
            }


            var textOnly = Regex.Replace(snippet, "<[^>]+>", "").Trim();

            return string.IsNullOrWhiteSpace(textOnly);
        }
    }

    
    private bool IsStrictNginx404Page(string content)
    {
        const string expected =
            "<html><head><title>404 Not Found</title></head>" +
            "<body><center><h1>404 Not Found</h1></center>" +
            "<hr><center>nginx</center></body></html>";

        var normalizedContent = Regex.Replace(content, @"\s+", "", RegexOptions.IgnoreCase);
        var normalizedExpected = Regex.Replace(expected,   @"\s+", "", RegexOptions.None);

        return normalizedContent.Equals(normalizedExpected, StringComparison.OrdinalIgnoreCase);
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
            if (e.InnerException != null && e.InnerException.Message.Contains("The remote certificate is invalid according to the validation procedure"))
            {
                _logger.LogError($"{url} === returned false. The remote certificate is invalid. Message: {e.Message}");
                return false;
            }
            if (e.InnerException != null && e.InnerException.Message.Contains("SSL Handshake failed with OpenSSL error"))
            {
                _logger.LogError($"{url} === returned false. The remote certificate is invalid. Message: {e.Message}");
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
                "related searches",
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
                "403 Error",
                "404 page not found",
                "404 not found",
                // "page not found",
                "this page can't be found", 
                "http error 404", 
                "no webpage was found", 
                "HTTP ERROR 404",
                // "Not Found",
                "HTTP 404", 
                "This site can't be reached",
                "This page could not be found", 
                // "404 Not Found", 
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
    
    
    
    private bool IsErrorPageByTitle(string htmlContent)
    {
        try
        {
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(htmlContent);

            var titleNode = htmlDoc.DocumentNode.SelectSingleNode("//title");
        
            if (titleNode == null)
            {
                return false;
            }

            var title = titleNode.InnerText.Trim();

            string[] errorTitleIndicators =
            {
                "HTTP 403",
                "502 Bad Gateway",
                "Error 404",
                "Page Not Found",
                "Not Found",
                "Internal Server Error",
                "Service Unavailable",
                "Gateway Timeout",
                "Bad Request",
                "This site can't be reached",
                "This page could not be found"
            };

            foreach (var indicator in errorTitleIndicators)
            {
                if (title.Contains(indicator, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning($"Error indicator in title: '{indicator}' (Full title: '{title}')");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error checking page title: {ex.Message}");
            return false;
        }
    }
}