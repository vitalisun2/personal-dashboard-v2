using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PersonalDashboard.V2.Contracts.Chat;

namespace PersonalDashboard.V2.Chat.Infrastructure;

public static class ChatInfrastructureModule
{
    public static IServiceCollection AddChatInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IChatConversationStore, ChatConversationStore>();
        return services;
    }
}
