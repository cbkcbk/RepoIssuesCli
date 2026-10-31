using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using RepoInfraCli.Features.Infra.Hosts;

namespace RepoInfraCli.Tests.Hosts;

public sealed class SshdContainer : IAsyncDisposable
{
    public const string ImageName = "repoinfra-test-sshd:1";
    public const string SudoPassword = "secret-pw";

    const string Dockerfile = """
        FROM ubuntu:24.04
        ENV DEBIAN_FRONTEND=noninteractive
        RUN apt-get update \
         && apt-get install -y --no-install-recommends systemd systemd-sysv openssh-server sudo ufw iptables cron ca-certificates \
         && ssh-keygen -A \
         && mkdir -p /keys \
         && ssh-keygen -q -t ed25519 -N '' -f /keys/id \
         && ssh-keygen -q -t ed25519 -N '' -f /keys/other \
         && useradd -m -s /bin/bash pw && echo 'pw:secret-pw' | chpasswd && usermod -aG sudo pw \
         && useradd -m -s /bin/bash nopw && echo 'nopw ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/nopw && chmod 440 /etc/sudoers.d/nopw \
         && for u in pw nopw; do mkdir -p /home/$u/.ssh && cp /keys/id.pub /home/$u/.ssh/authorized_keys && chown -R $u:$u /home/$u/.ssh && chmod 700 /home/$u/.ssh && chmod 600 /home/$u/.ssh/authorized_keys; done \
         && systemctl enable ssh \
         && systemctl disable cron
        CMD ["/sbin/init"]
        """;

    const string DockerImageName = "repoinfra-test-sshd-docker:1";

    const string DockerDockerfile = $"""
        FROM {ImageName}
        RUN apt-get update \
         && apt-get install -y --no-install-recommends curl gnupg \
         && install -m 0755 -d /etc/apt/keyrings \
         && curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc \
         && echo "deb [signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu noble stable" > /etc/apt/sources.list.d/docker.list \
         && apt-get update \
         && apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin
        """;

    readonly IContainer container;
    readonly string folder;

    SshdContainer(IContainer container, string folder)
    {
        this.container = container;
        this.folder = folder;
    }

    public string Address => container.Hostname;

    public int Port => container.GetMappedPublicPort(22);

    public string KeyFile => Path.Combine(folder, "id");

    public string PublicKey { get; private set; } = "";

    public string HostKeyLine { get; private set; } = "";

    public string OtherKeyLine { get; private set; } = "";

    public string KnownHostsName => Port == 22 ? Address : $"[{Address}]:{Port}";

    public static Task<SshdContainer> StartAsync() => StartAsync(null);

    public static Task<SshdContainer> StartWithDockerAsync(INetwork network) => StartAsync(network);

    static async Task<IImage> BuildImageAsync(string folder, string name, string dockerfile)
    {
        var context = Path.Combine(folder, name.Replace(':', '-'));
        Directory.CreateDirectory(context);
        await File.WriteAllTextAsync(Path.Combine(context, "Dockerfile"), dockerfile.ReplaceLineEndings("\n"));
        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(context)
            .WithDockerfile("Dockerfile")
            .WithName(name)
            .WithCleanUp(false)
            .WithDeleteIfExists(false)
            .Build();
        await image.CreateAsync();
        return image;
    }

    static async Task<SshdContainer> StartAsync(INetwork? dockerNetwork)
    {
        var folder = Directory.CreateTempSubdirectory("infra-sshd-").FullName;
        var image = await BuildImageAsync(folder, ImageName, Dockerfile);
        if (dockerNetwork is not null)
            image = await BuildImageAsync(folder, DockerImageName, DockerDockerfile);

        var builder = new ContainerBuilder(image)
            .WithPrivileged(true)
            .WithTmpfsMount("/run")
            .WithTmpfsMount("/run/lock")
            .WithPortBinding(22, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(22));
        if (dockerNetwork is not null)
            builder = builder.WithBindMount("/var/run/docker.sock", "/var/run/docker.sock").WithNetwork(dockerNetwork);
        var container = builder.Build();
        await container.StartAsync();

        var sshd = new SshdContainer(container, folder);
        await File.WriteAllBytesAsync(sshd.KeyFile, await container.ReadFileAsync("/keys/id"));
        sshd.PublicKey = Encoding.UTF8.GetString(await container.ReadFileAsync("/keys/id.pub")).Trim();
        sshd.HostKeyLine = HostKey(await container.ReadFileAsync("/etc/ssh/ssh_host_ed25519_key.pub"));
        sshd.OtherKeyLine = HostKey(await container.ReadFileAsync("/keys/other.pub"));
        return sshd;
    }

    static string HostKey(byte[] publicKey) => string.Join(' ', Encoding.UTF8.GetString(publicKey).Trim().Split(' ').Take(2));

    public string KnownHostsFile(string name, params string[] keyLines)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, string.Concat(keyLines.Select(line => $"{KnownHostsName} {line}\n")));
        return path;
    }

    public SshHostConnector Connector(string knownHosts) => new(new SshConnectorOptions
    {
        ConfigFiles = [],
        KnownHostsFiles = [knownHosts],
        IdentityFiles = [KeyFile],
        ConnectTimeout = TimeSpan.FromSeconds(30),
    });

    public async Task<string> ExecAsRootAsync(params string[] command)
    {
        var result = await container.ExecAsync(command);
        return result.Stdout;
    }

    public async ValueTask DisposeAsync()
    {
        await container.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }
}
