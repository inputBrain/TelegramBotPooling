using System;
using Microsoft.Extensions.Logging;
using TelegramBotPooling.Abstracts;

namespace TelegramBotPooling.Services;

public class PollingService : PollingServiceBase<ReceiverService>
{
    public PollingService(IServiceProvider serviceProvider, ILogger<PollingService> logger) : base(serviceProvider, logger)
    {
    }
}