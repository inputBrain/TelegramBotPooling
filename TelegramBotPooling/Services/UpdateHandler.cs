using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using TelegramBotPooling.Configs;

namespace TelegramBotPooling.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly ILogger<UpdateHandler> _logger;
    private readonly IConfiguration _configuration;

    private readonly ITelegramBotClient _botClient;

    public UpdateHandler(ILogger<UpdateHandler> logger, IConfiguration configuration, ITelegramBotClient botClient)
    {
        _logger = logger;
        _configuration = configuration;
        _botClient = botClient;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken cancellationToken)
    {
        var botConfig = _configuration.GetSection("TelegramBot").Get<TelegramBotConfig>()!;
        var handler = update switch
        {
            { Message: { } message }                       => BotOnMessageReceived(botConfig, message, cancellationToken),
            { EditedMessage: { } message }                 => BotOnMessageReceived(botConfig, message, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(update), update, null)
        };

        await handler;
    }

    private async Task BotOnMessageReceived(TelegramBotConfig botConfig, Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Receive message type: {MessageType}", message.Type);
        if (message.Text is not { } messageText)
            return;

        var action = messageText.Split(' ')[0] switch
        {
            "/forcestart"                           => StartWebsiteHandler(_botClient, botConfig, message, cancellationToken),
            "/forcestart@AxLinkKeyboard_bot"        => StartWebsiteHandler(_botClient, botConfig, message, cancellationToken),
            _                                       => throw new ArgumentOutOfRangeException()
        };
        var sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    public static async Task<Message> StartWebsiteHandler(ITelegramBotClient botClient, TelegramBotConfig botConfig, Message message, CancellationToken cancellationToken)
    {



        return await botClient.SendTextMessageAsync(
            chatId: botConfig.PrivateChatId,
            text: "Choose",
            cancellationToken: cancellationToken);
    }

    public async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var ErrorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogInformation("HandleError: {ErrorMessage}", ErrorMessage);

        if (exception is RequestException)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}