using System.Threading;
using System.Threading.Tasks;

namespace TelegramBotPooling.Api.Service;

public interface IBaseParser
{
    Task<T?> ParseGet<T>(string url, CancellationToken cancellationToken) where T : class;
}