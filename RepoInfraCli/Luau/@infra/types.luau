--!strict

export type Secret = { __secret: string }

export type Output<T> = { __output: T }

export type Input<T> = T | Output<T>

export type FileRef = { __file: string }

export type EnvValue = Input<string> | Secret

export type Domain = { hostname: string }

export type Network = { name: string }

export type Bucket = {
    name: string,
    endpoint: Output<string>,
}

export type Database = {
    name: string,
    host: Input<string>,
    connection_string: Secret,
}

export type FirewallRule = {
    tcp: (number | string)?,
    udp: (number | string)?,
    icmp: boolean?,
    from: { string }?,
    to: { string }?,
}

-- Host tasks ------------------------------------------------------------------

export type ServiceState = "started" | "stopped" | "restarted" | "reloaded" | "running"

export type AptOptions = {
    update: boolean?,
    upgrade: ("safe" | "full" | "dist")?,
    packages: { string }?,
    absent: { string }?,
}

export type AptRepoOptions = {
    url: string,
    key: string,
    components: { string }?,
}

export type UserOptions = {
    shell: string?,
    groups: { string }?,
    append: boolean?,
    authorized_keys: { FileRef | string }?,
}

export type Handler = { __handler: string }

export type FileOptions = {
    content: string?,
    source: FileRef?,
    mode: string?,
    owner: string?,
    notify: Handler?,
}

export type UfwOptions = {
    default: ("deny" | "allow")?,
    allow: { FirewallRule }?,
}

export type ServiceOptions = {
    enabled: boolean?,
    state: ServiceState?,
}

export type HandlerOptions = {
    service: string,
    state: ServiceState,
}

export type ComposeHealthcheck = {
    http: string?,
    test: { string }?,
    interval: string?,
}

export type ComposeService = {
    image: string,
    restart: ("no" | "always" | "on-failure" | "unless-stopped")?,
    ports: { string }?,
    networks: { Network }?,
    volumes: { string }?,
    environment: { [string]: EnvValue }?,
    command: { string }?,
    labels: { [string]: string }?,
    healthcheck: ComposeHealthcheck?,
    depends_on: { string }?,
}

export type ComposeOptions = {
    dir: string?,
    services: { [string]: ComposeService },
    volumes: { [string]: {} }?,
}

export type Host = {
    name: string,
    apt: (self: Host, options: AptOptions) -> (),
    apt_repo: (self: Host, name: string) -> (options: AptRepoOptions) -> (),
    user: (self: Host, name: string) -> (options: UserOptions) -> (),
    file: (self: Host, path: string) -> (options: FileOptions) -> (),
    ufw: (self: Host, options: UfwOptions) -> (),
    service: (self: Host, name: string) -> (options: ServiceOptions) -> (),
    handler: (self: Host, options: HandlerOptions) -> Handler,
    compose: (self: Host, project: string) -> (options: ComposeOptions) -> (),
    shell: (self: Host, command: string) -> (),
}

export type HostConnection = {
    user: string,
    port: number?,
    become: boolean?,
}

export type HostOptions = {
    address: Input<string>,
    user: string,
    port: number?,
    become: boolean?,
}

-- Apps and plays --------------------------------------------------------------

export type Build = { __build: string }

export type AppOptions = {
    build: Build,
    deploy: (host: Host, image: string) -> (),
}

export type App = { name: string }

export type Role = (host: Host) -> ()

export type PlayOptions = {
    hosts: { Host },
    roles: { Role }?,
    apps: { App }?,
}

export type Exports = {
    hosts: { [string]: Host }?,
    roles: { [string]: Role }?,
    networks: { [string]: Network }?,
    databases: { [string]: Database }?,
}

export type ProjectOptions = {
    prune: boolean?,
}

return {}
