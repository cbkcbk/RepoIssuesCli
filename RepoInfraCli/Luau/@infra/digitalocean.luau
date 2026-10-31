--!strict
-- Type stubs; RepoInfraCli provides the implementation at runtime.
local types = require("@infra/types")

type Output<T> = types.Output<T>
type Input<T> = types.Input<T>

export type SshKey = {
    id: Output<string>,
    fingerprint: Output<string>,
}

export type Droplet = {
    id: Output<string>,
    ipv4_address: Output<string>,
    ipv4_address_private: Output<string>,
    ipv6_address: Output<string>,
    host: (self: Droplet, connection: types.HostConnection) -> types.Host,
}

export type Vpc = {
    id: Output<string>,
}

export type FirewallRule = types.FirewallRule

export type SshKeyOptions = {
    public_key: types.FileRef,
    protect: boolean?,
}

export type DropletOptions = {
    image: string,
    size: string,
    region: string,
    ssh_keys: { Input<string> }?,
    tags: { string }?,
    vpc: Input<string>?,
    backups: boolean?,
    monitoring: boolean?,
    protect: boolean?,
}

export type FirewallOptions = {
    droplets: { Input<string> }?,
    tags: { string }?,
    inbound: { FirewallRule }?,
    outbound: { FirewallRule }?,
    protect: boolean?,
}

export type VpcOptions = {
    region: string,
    ip_range: string?,
    protect: boolean?,
}

export type SpacesBucket = types.Bucket

export type LifecycleRule = {
    prefix: string,
    expire_days: number,
}

export type SpacesBucketOptions = {
    region: string,
    acl: ("private" | "public-read")?,
    versioning: boolean?,
    lifecycle: { LifecycleRule }?,
    protect: boolean?,
}

export type DatabaseClusterOptions = {
    engine: "pg",
    version: string,
    size: string,
    region: string,
    nodes: number?,
    trusted_sources: { Input<string> }?,
    protect: boolean?,
}

export type ManagedDatabaseOptions = {
    user: string?,
}

export type DatabaseCluster = {
    id: Output<string>,
    host: Output<string>,
    database: (name: string) -> (options: ManagedDatabaseOptions) -> types.Database,
}

export type ExistingDatabaseCluster = {
    id: Output<string>,
    host: Output<string>,
    database: (name: string) -> types.Database,
}

export type Existing = {
    database_cluster: (name: string) -> ExistingDatabaseCluster,
}

export type ProviderOptions = {
    token: types.Secret,
    spaces_access_key: types.Secret?,
    spaces_secret_key: types.Secret?,
}

export type Provider = {
    ssh_key: (name: string) -> (options: SshKeyOptions) -> SshKey,
    droplet: (name: string) -> (options: DropletOptions) -> Droplet,
    firewall: (name: string) -> (options: FirewallOptions) -> (),
    vpc: (name: string) -> (options: VpcOptions) -> Vpc,
    spaces_bucket: (name: string) -> (options: SpacesBucketOptions) -> SpacesBucket,
    database_cluster: (name: string) -> (options: DatabaseClusterOptions) -> DatabaseCluster,
    existing: Existing,
}

local digitalocean = {}

function digitalocean.provider(options: ProviderOptions): Provider
    error("native")
end

return digitalocean
