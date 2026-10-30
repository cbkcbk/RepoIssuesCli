--!strict
-- Type stubs; RepoInfraCli provides the implementation at runtime.
local types = require("@infra/types")

export type EntryPoint = {
    address: string,
    trusted_ips: { string }?,
}

export type Resolver = { name: string }

export type ResolverOptions = {
    email: string,
    storage: string,
    dns_challenge: string?,
}

export type DockerProvider = {
    exposed_by_default: boolean?,
    network: types.Network?,
}

export type StaticConfig = {
    entrypoints: { [string]: EntryPoint },
    resolvers: { Resolver }?,
    docker: DockerProvider?,
    log_level: ("DEBUG" | "INFO" | "WARN" | "ERROR")?,
}

export type RouteOptions = {
    host: string | types.Domain,
    port: number,
    router: string?,
    entrypoint: string?,
    resolver: Resolver?,
    middlewares: { string }?,
}

local traefik = {}

function traefik.resolver(name: string): (options: ResolverOptions) -> Resolver
    error("native")
end

function traefik.args(config: StaticConfig): { string }
    error("native")
end

function traefik.labels(route: RouteOptions): { [string]: string }
    error("native")
end

return traefik
