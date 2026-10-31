using RepoInfraCli.Cli.Commands;
using RepoInfraCli.Cli.Commands.Secrets;
using RepoInfraCli.Features.Infra.Apps;
using RepoInfraCli.Features.Infra.Cloud;
using RepoInfraCli.Features.Infra.Hosts;
using RepoInfraCli.Features.Infra.Plan;
using RepoInfraCli.Features.Infra.Secrets;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RepoInfraCli.Cli;

public static class InfraApp
{
    public static CommandApp Create(
        IAnsiConsole output, IAnsiConsole errors, TextReader? input = null, SecretEnvironment? secrets = null,
        Action<ITypeRegistrarFrontend>? services = null)
    {
        var machine = secrets ?? SecretEnvironment.Default;
        var app = new CommandApp();
        app.Configure(config =>
        {
            RepoCliApp.Configure(config, "infra", typeof(InfraApp).Assembly, output, errors);
            config.Settings.Registrar.RegisterInstance(new ErrorConsole(errors));
            config.Settings.Registrar.RegisterInstance(input ?? TextReader.Null);
            config.Settings.Registrar.RegisterInstance(machine);
            config.Settings.Registrar.RegisterInstance<ISecretResolver>(new ChainedSecretResolver(machine));
            config.Settings.Registrar.RegisterInstance<IBuildTools>(DotnetBuildTools.Default);

            config.AddCommand<InitCommand>("init")
                .WithExample("init", "--json");
            config.AddCommand<ValidateCommand>("validate")
                .WithExample("validate", "--env", "prod")
                .WithExample("validate", "--json")
                .WithExample("validate", "-v");

            config.Settings.Registrar.RegisterInstance<IHostConnector>(new SshHostConnector());
            var cloud = CloudServices.CreateDefault();
            config.Settings.Registrar.RegisterInstance(cloud);
            config.Settings.Registrar.RegisterInstance<ILiveValues>(cloud.Live);
            config.Settings.Registrar.RegisterInstance<IResourcePlanner>(cloud.Planner);
            config.Settings.Registrar.RegisterInstance<IResourceApplier>(cloud.Applier);
            config.AddCommand<PlanCommand>("plan")
                .WithExample("plan")
                .WithExample("plan", "--play", "edge", "--host", "web-1")
                .WithExample("plan", "--exit-code", "--json");
            config.AddCommand<ApplyCommand>("apply")
                .WithExample("apply")
                .WithExample("apply", "--play", "build", "--yes")
                .WithExample("apply", "--resources-only", "--allow-destroy", "--yes");
            config.AddCommand<AdoptCommand>("adopt")
                .WithExample("adopt", "web-1")
                .WithExample("adopt", "firewall:web", "--yes");
            config.AddCommand<ShowCommand>("show")
                .WithExample("show")
                .WithExample("show", "web-1", "--field", "ipv4_address");
            config.AddCommand<DestroyCommand>("destroy")
                .WithExample("destroy")
                .WithExample("destroy", "web-1", "--yes", "--confirm", "snappy-prod");
            config.AddCommand<RestoreCommand>("restore")
                .WithExample("restore", "api", "--list")
                .WithExample("restore", "api", "--into", "api_drill")
                .WithExample("restore", "api", "--at", "2026-10-30T03:00Z")
                .WithExample("restore", "api", "--dump", "postgres/api/api-20261030T030000Z.dump", "--yes");
            config.AddBranch("db", db =>
            {
                db.SetDescription("Postgres databases declared in infra.luau.");
                db.AddCommand<DbCopyCommand>("copy")
                    .WithExample("db", "copy", "api", "--to", "local:api_dev", "--scrub", "scrub.sql", "--env", "prod")
                    .WithExample("db", "copy", "main/reports", "--to", "local:reports", "--scrub", "scrub.sql", "--migrate");
            });
            config.AddBranch("secrets", secretsBranch =>
            {
                secretsBranch.SetDescription("Encrypted secrets in .repo-infra/secrets.yaml, resolved by secret 'x/y' in infra.luau.");
                secretsBranch.AddCommand<SecretsInitCommand>("init").WithExample("secrets", "init");
                secretsBranch.AddCommand<SecretsSetCommand>("set").WithExample("secrets", "set", "prod/db/password", "--stdin");
                secretsBranch.AddCommand<SecretsGenerateCommand>("generate").WithExample("secrets", "generate", "postgres/api", "--length", "40");
                secretsBranch.AddCommand<SecretsListCommand>("list").WithExample("secrets", "list", "--json");
                secretsBranch.AddCommand<SecretsShowCommand>("show").WithExample("secrets", "show", "postgres/api");
                secretsBranch.AddCommand<SecretsKeyCommand>("key").WithExample("secrets", "key");
                secretsBranch.AddBranch("recipients", recipients =>
                {
                    recipients.SetDescription("Who can decrypt the secrets: people and CI pipelines, one public key each.");
                    recipients.AddCommand<RecipientsAddCommand>("add").WithExample("secrets", "recipients", "add", "ci-github", "p256:MFkw...");
                    recipients.AddCommand<RecipientsRemoveCommand>("remove").WithExample("secrets", "recipients", "remove", "ci-github");
                });
            });
            config.AddCommand<HookCommand>("hook")
                .WithExample("hook", "shell");
#if DEBUG
            config.AddCommand<RepoCli.Core.Cli.Plugin.GeneratePluginCommand<Plugin.InfraPlugin>>(RepoCli.Core.Cli.Plugin.PluginGenerator.CommandName);
#endif
            services?.Invoke(config.Settings.Registrar);
        });
        return app;
    }
}