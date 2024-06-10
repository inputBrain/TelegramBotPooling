using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace TelegramBotPooling.Services;

public class MessageService : IMessageService
{
    private readonly ITelegramBotClient _botClient;

    public MessageService(ITelegramBotClient botClient)
    {
        _botClient = botClient;
    }


    public async Task SendInfo(Dictionary<string, (int totalLinks, int accessibleLinks)>  linksGroupByNameAndThemCount, int openedLinksCount, long chatId, CancellationToken cancellationToken)
    {
        var header = "<b>Info!</b>\n" +
                     "<b>From:</b> Google tabs <a href=\"https://docs.google.com/spreadsheets/d/1GR64t3cbiDYEtHFIiU9mxKoJvXYF304nqhgsVDxQ4MI/edit#gid=0/\">MWA - Avto Test link</a> \n" +
                     "<b>Message:</b> Daily test\n\n" +
                     $"<b>Context:</b> Accessible links count: {openedLinksCount}\n";

        await _sendMessagePart(header, chatId, cancellationToken);

        var currentMessage = new StringBuilder();

        foreach (var (groupName, count) in linksGroupByNameAndThemCount)
        {
            var groupText = $"{groupName}: {count.accessibleLinks} of {count.totalLinks}\n";

            if (currentMessage.Length + groupText.Length > 4096)
            {
                await _sendMessagePart(currentMessage.ToString(), chatId, cancellationToken);
                currentMessage.Clear();
            }

            currentMessage.Append(groupText);
        }

        if (currentMessage.Length > 0)
        {
            await _sendMessagePart(currentMessage.ToString(), chatId, cancellationToken);
        }
    }



    public async Task SendError(Dictionary<string, List<string>> linksWithNames, int notOpenedLinksCount, long chatId, CancellationToken cancellationToken)
    {
        var header = "<b>Error!</b>\n" +
                     "<b>From:</b> Google tabs <a href=\"https://docs.google.com/spreadsheets/d/1GR64t3cbiDYEtHFIiU9mxKoJvXYF304nqhgsVDxQ4MI/edit#gid=0\">MWA - Avto Test link</a> \n" +
                     "<b>Message:</b> The links have stopped working \n\n" +
                     $"<b>Context:</b> Not Accessible links count: {notOpenedLinksCount}\n";

        await _sendMessagePart(header, chatId, cancellationToken);

        var currentMessage = new StringBuilder();

        foreach (var linkWithName in linksWithNames)
        {
            foreach (var site in linkWithName.Value)
            {
                var linkText = $"{linkWithName.Key}: {site}\n";

                if (currentMessage.Length + linkText.Length > 4096)
                {
                    await _sendMessagePart(currentMessage.ToString(), chatId, cancellationToken);
                    currentMessage.Clear();
                }

                currentMessage.Append(linkText);
            }
        }

        if (currentMessage.Length > 0)
        {
            await _sendMessagePart(currentMessage.ToString(), chatId, cancellationToken);
        }
    }

    private async Task _sendMessagePart(string message, long chatId, CancellationToken cancellationToken)
    {
        await _botClient.SendTextMessageAsync(chatId, message, cancellationToken: cancellationToken, disableWebPagePreview: true, parseMode: ParseMode.Html);
    }
}