using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Framing;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;
using OpenEleven.Server.Transport;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Configuration;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything that is not a transport: protocol codecs, global state,
    /// dispatch and data access. Shared by the host and by the tests so they cannot
    /// drift apart.
    /// </summary>
    public static IServiceCollection AddOpenElevenCore(
        this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<ServerOptions>()
            .Bind(configuration.GetSection(ServerOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddSingleton<IValidateOptions<ServerOptions>, ServerOptionsValidator>();

        // A relative SQLite path must mean the same file however the server was started,
        // so it is anchored before anything reads the connection string.
        services.PostConfigure<ServerOptions>(o =>
        {
            if (o.Database.Provider == DatabaseProvider.Sqlite)
                o.Database.ConnectionString = DatabasePath.ResolveSqlite(o.Database.ConnectionString);
        });

        var options = configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>()
                      ?? new ServerOptions();

        if (options.Database.Provider == DatabaseProvider.Sqlite)
            options.Database.ConnectionString =
                DatabasePath.ResolveSqlite(options.Database.ConnectionString);

        // --- singletons: stateless codecs and the one global state store ---
        // The cipher keys are shared by every supported title, so they come from
        // GameCrypto constants rather than per-title configuration.
        services.AddSingleton(new XorCipher(GameCrypto.XorKey));
        services.AddSingleton(new BlowfishEcb(GameCrypto.BlowfishKey));
        services.AddSingleton<PacketCodec>();
        services.AddSingleton<KvReader>();
        services.AddSingleton<KvWriter>();
        services.AddSingleton<ProtocolCodecs>();

        services.AddSingleton<Hub>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PendingLoginStore>();
        services.AddSingleton<ServerCatalog>();
        services.AddSingleton<LobbyCatalog>();
        services.AddSingleton<ProtocolTextPolicy>();
        services.AddSingleton<ChatTextPolicy>();
        services.AddSingleton<WebAssets>();
        services.AddScoped<GameIdAuthService>();
        services.AddScoped<RegistrationService>();

        services.AddSingleton(_ => CommandRegistry.Build(typeof(ServiceCollectionExtensions).Assembly));
        services.AddSingleton<UnknownCommandLog>();
        services.AddSingleton<CommandDispatcher>();

        // --- scoped: one DI scope per dispatched command ---
        foreach (var handlerType in CommandRegistry.DiscoverHandlerTypes(
                     typeof(ServiceCollectionExtensions).Assembly))
            services.AddScoped(handlerType);

        services.AddDbContext<GameDbContext>(dbOptions =>
        {
            var db = options.Database;
            switch (db.Provider)
            {
                case DatabaseProvider.MySql:
                    dbOptions.UseMySql(db.ConnectionString, ServerVersion.AutoDetect(db.ConnectionString));
                    break;
                default:
                    dbOptions.UseSqlite(db.ConnectionString);
                    break;
            }
        });

        services.AddScoped<IPlayerRepository, PlayerRepository>();
        services.AddScoped<IAccountRepository, AccountRepository>();
        services.AddScoped<ICatalogRepository, CatalogRepository>();
        services.AddScoped<IMatchRepository, MatchRepository>();
        services.AddScoped<DatabaseInitializer>();

        return services;
    }
}
