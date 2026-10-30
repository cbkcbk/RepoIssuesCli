--!strict
local infra = require("@infra")
local digitalocean = require("@infra/digitalocean")
local cloudflare = require("@infra/cloudflare")
local compose = require("@infra/compose")
local traefik = require("@infra/traefik")
local postgres = require("@infra/postgres")
local dotnet = require("@infra/dotnet")
local repo = require("@infra/repo")

type Host = infra.Host

type FirewallRule = infra.FirewallRule

local secret = infra.secret
local file = infra.file

local deployPub = file "~/.ssh/deploy.pub"

local sshAccess: FirewallRule = { tcp = 22, from = { "84.212.0.0/16", "10.0.0.0/24" } }
local httpsFromCloudflare: FirewallRule = { tcp = 443, from = cloudflare.ip_ranges }

infra.project "snappy" {
    prune = true,
}

-- Providers -------------------------------------------------------------------

local doProvider = digitalocean.provider { token = secret "digitalocean/token" }
local cfProvider = cloudflare.provider { api_token = secret "cloudflare/token" }
local platform = repo.provider { url = "git@github.com:snappy/platform-infra.git", ref = "v1" }

-- Resources (found by ownership tag, diffed against live state) ---------------

local zone = cfProvider.zone "snappy.no"

local deployKey = doProvider.ssh_key "deploy" {
    public_key = deployPub,
}

local web1 = doProvider.droplet "web-1" {
    image = "ubuntu-24-04-x64",
    size = "s-2vcpu-4gb",
    region = "fra1",
    ssh_keys = { deployKey.fingerprint },
    tags = { "web" },
}

doProvider.firewall "web" {
    droplets = { web1.id },
    inbound = { sshAccess, httpsFromCloudflare },
}

local apiRecord = zone.record "api" { type = "A", content = web1.ipv4_address, proxied = true }
zone.record "@" { type = "A", content = web1.ipv4_address, proxied = true, protect = true }

local backups = doProvider.spaces_bucket "snappy-backups" {
    region = "fra1",
    lifecycle = { { prefix = "postgres/", expire_days = 14 } },
    protect = true,
}

local mainDb = doProvider.database_cluster "main" {
    engine = "pg",
    version = "17",
    size = "db-s-1vcpu-1gb",
    region = "fra1",
    trusted_sources = { web1.id },
    protect = true,
}

local legacyDb = doProvider.existing.database_cluster "legacy-pg"

-- Inventory -------------------------------------------------------------------

local web = web1:host { user = "root" }
local build = infra.host "build-1" {
    address = "10.0.0.21",
    user = "ubuntu",
    become = true,
}

-- Edge ------------------------------------------------------------------------

local edgeNet = compose.network "edge"

local certs = traefik.resolver "cf" {
    email = "ops@snappy.no",
    dns_challenge = "cloudflare",
    storage = "/acme/acme.json",
}

-- Data ------------------------------------------------------------------------

local dbNet = compose.network "db"

local apiDb = postgres.database "api" {
    version = "17",
    password = secret "postgres/api",
    network = dbNet,
    data = "/srv/postgres/api",
}

local reportsDb = mainDb.database "reports" { user = "reports" }
local crmDb: infra.Database = legacyDb.database "crm"

-- Roles (facts read over SSH, diffed, changes pushed) -------------------------

local function baseline(h: Host)
    h:apt {
        update = true,
        upgrade = "safe",
        packages = { "ufw", "fail2ban", "unattended-upgrades", "curl" },
    }
    
    h:user "deploy" {
        shell = "/bin/bash",
        authorized_keys = { deployPub },
    }

    local restartSsh = h:handler { service = "ssh", state = "restarted" }

    h:file "/etc/ssh/sshd_config.d/10-hardening.conf" {
        content = [[
PasswordAuthentication no
PermitRootLogin prohibit-password
]],
        notify = restartSsh,
    }

    h:ufw { default = "deny", allow = { sshAccess } }
end

local function docker(h: Host)
    h:apt_repo "docker" {
        url = "https://download.docker.com/linux/ubuntu",
        key = "https://download.docker.com/linux/ubuntu/gpg",
    }
    h:apt { packages = { "docker-ce", "docker-compose-plugin" } }
    h:service "docker" { enabled = true, state = "running" }
    h:user "deploy" { groups = { "docker" }, append = true }
end

local function edge(h: Host)
    h:ufw { allow = { httpsFromCloudflare } }

    h:compose "traefik" {
        dir = "/opt/traefik",
        services = {
            traefik = {
                image = "traefik:v3.5",
                restart = "unless-stopped",
                ports = { "443:443" },
                networks = { edgeNet },
                volumes = {
                    "/var/run/docker.sock:/var/run/docker.sock:ro",
                    "./acme:/acme",
                },
                environment = { CF_DNS_API_TOKEN = secret "cloudflare/dns_token" },
                command = traefik.args {
                    entrypoints = {
                        websecure = { address = ":443", trusted_ips = cloudflare.ip_ranges },
                    },
                    resolvers = { certs },
                    docker = { exposed_by_default = false, network = edgeNet },
                },
            },
        },
    }
end

local function database(h: Host)
    h:compose "postgres" {
        dir = "/opt/postgres",
        services = {
            postgres = postgres.server(apiDb),
            backup = postgres.backup(apiDb) {
                bucket = backups,
                credentials = secret "spaces/backups",
                schedule = "0 3 * * *",
                prefix = "postgres/api",
            },
            backup_reports = postgres.backup(reportsDb) {
                bucket = backups,
                credentials = secret "spaces/backups",
                schedule = "30 3 * * *",
                prefix = "postgres/reports",
            },
        },
    }
end

local function buildAgent(h: Host)
    h:apt { packages = { "dotnet-sdk-10.0", "git" } }
end

-- Apps ------------------------------------------------------------------------

local api = infra.app "api" {
    build = dotnet.container {
        project = "Api/Api.csproj",
        registry = "registry.digitalocean.com/snappy",
        tag = infra.git.sha,
    },

    deploy = function(h: Host, image: string)
        h:compose "api" {
            dir = "/opt/api",
            services = {
                api = {
                    image = image,
                    restart = "unless-stopped",
                    networks = { edgeNet, dbNet },
                    environment = {
                        ASPNETCORE_URLS = "http://+:8080",
                        ConnectionStrings__Db = apiDb.connection_string,
                        ConnectionStrings__Reports = reportsDb.connection_string,
                        ConnectionStrings__Crm = crmDb.connection_string,
                    },
                    labels = traefik.labels {
                        host = apiRecord,
                        port = 8080,
                        resolver = certs,
                    },
                    healthcheck = { http = "http://localhost:8080/health" },
                },
            },
        }
    end,
}

-- Plays -----------------------------------------------------------------------

infra.play "edge" {
    hosts = { web },
    roles = { baseline, docker, edge },
}

infra.play "data" {
    hosts = { web },
    roles = { database },
}

infra.play "apps" {
    hosts = { web },
    apps = { api },
}

infra.play "build" {
    hosts = { build },
    roles = { baseline, docker, buildAgent, platform.role "node_exporter" },
}

infra.play "nas" {
    hosts = { platform.host "nas" },
    roles = { baseline },
}

-- Exports (what other repos can look up with repo.provider) -------------------

local exports: infra.Exports = {
    hosts = { web = web },
    roles = { baseline = baseline, docker = docker },
    networks = { edge = edgeNet },
    databases = { reports = reportsDb },
}

return exports
