using System.Threading;
using System.Threading.Tasks;

namespace TelegramBotPooling.Abstracts;

public interface IReceiverService
{
    Task ReceiveAsync(CancellationToken stoppingToken);
}