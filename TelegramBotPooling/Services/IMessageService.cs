using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TelegramBotPooling.Services;

public interface IMessageService
{
    Task SendInfo(
        Dictionary<string, (int totalLinks, int accessibleLinks)> linksGroupByNameAndThemCount,
        int openedLinksCount,
        long chatId,
        CancellationToken cancellationToken
    );

    Task SendError(
        Dictionary<string, List<string>> linksWithNames,
        int notOpenedLinksCount,
        long chatId,
        CancellationToken cancellationToken
    );
}