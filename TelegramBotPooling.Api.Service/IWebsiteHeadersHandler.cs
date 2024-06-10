using System.Threading.Tasks;

namespace TelegramBotPooling.Api.Service;

public interface IWebsiteHeadersHandler
{
    Task<bool> HeaderHandlerAsync(string url);
}