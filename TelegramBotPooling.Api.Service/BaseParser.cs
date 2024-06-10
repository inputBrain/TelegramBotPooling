using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TelegramBotPooling.Api.Service;

public class BaseParser : IBaseParser
{
    private readonly HttpClient _client;
    private readonly ILogger<BaseParser> _logger;


    public BaseParser(HttpClient client, ILogger<BaseParser> logger)
    {
        _client = client;
        _logger = logger;
    }


    public async Task<T?> ParseGet<T>(string url, CancellationToken cancellationToken) where T : class
    {
        var response = await _client.GetAsync(url, cancellationToken);

        var scriptText = await response.Content.ReadAsStringAsync(cancellationToken);
        var appData = JsonSerializer.Deserialize<T>(scriptText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return appData;
    }
}