import * as fs from 'fs';
import * as path from 'path';

/** An OpenID Connect issuer the server accepts logins from (see Bassia.Server/ServerConfig.cs, [[auth.oidc]]). */
export interface OidcProvider {
  name: string;
  issuer: string;
  audience: string;
  client_id?: string;
  scopes?: string;
}

/**
 * One deployment of the Bassia server - an environment such as staging or production - as `config/<name>.json` holds
 * it. Everything region-specific is here, so another region is another file, not a fork of the stacks.
 */
export interface EnvironmentConfig {
  /** The environment's name; it prefixes every stack and resource name. */
  envName: string;
  /** The AWS account, or empty for the one the deploying credentials belong to. */
  account?: string;
  region: string;
  /** The region's availability zones the VPC spans (three). */
  availabilityZones: string[];
  /** The server's DNS name (users reach https://<domainName>); null for an environment reached at the load balancer's name over HTTP (staging only). */
  domainName: string | null;
  /** The Route 53 hosted zone for the domain (its name and id), when the stack is to create the record and a validated certificate. */
  hostedZoneName?: string | null;
  hostedZoneId?: string | null;
  /** An ACM certificate for the domain made outside the stacks, instead of one they create. */
  certificateArn?: string | null;
  natGateways: number;
  server: {
    cpu: number;
    memoryMiB: number;
    /** The task's ephemeral storage for the git cache and scratch repositories (21-200 GiB). */
    ephemeralGiB: number;
    minTasks: number;
    maxTasks: number;
    /** The image tag CI published (a version tag, v<semver>). */
    imageTag: string;
  };
  database: {
    engineVersion: string;
    minAcu: number;
    maxAcu: number;
    backupDays: number;
  };
  workers: {
    cpu: number;
    memoryMiB: number;
    ephemeralGiB: number;
    /** Whether workers run on Fargate Spot (interruptible, ~70% cheaper). */
    spot: boolean;
    imageTag: string;
    /** A Secrets Manager secret (name) whose value becomes the workers' ANTHROPIC_API_KEY, or null. */
    agentSecretName?: string | null;
  };
  organization?: string;
  /** Registered users who hold every permission (they can act before they are registered). */
  admins: string[];
  defaultPermission: 'none' | 'read';
  oidc: OidcProvider[];
  githubClientId?: string | null;
  /**
   * The client secrets of the dashboard's sign-in in a browser: a Secrets Manager secret (name), made outside the stacks,
   * holding a JSON object - `github` for the GitHub OAuth app's secret (its web flow needs one), and an OIDC provider's
   * name for that provider's client secret - and which of those keys it has. Null: browsers sign in only with OIDC
   * providers whose client is public (PKCE).
   */
  webSignIn?: { secretName: string; github: boolean; oidc: string[] } | null;
  /** Thresholds of the alarms on the server's own metrics (defaults: 50 lock conflicts in 5 minutes, 20 landings queued). */
  metricAlarms?: { lockConflictsPer5Minutes?: number; mergeQueueDepth?: number };
  /** Where alarms and budget alerts are sent. */
  alarmEmail: string;
  monthlyBudgetUsd: number;
  /** When the histories' garbage is collected (an EventBridge schedule expression). */
  gcSchedule: string;
  /** Requests per 5 minutes from one address before the firewall blocks it (agent workers share the NAT's addresses). */
  rateLimitPer5Minutes: number;
  /** The size of the bucket above which an alarm goes off, in GiB. */
  storageAlarmGiB: number;
  /** Whether the data outlives the stacks (production) or goes with them (staging). */
  retainData: boolean;
}

/** Reads and checks `config/<name>.json`. */
export function loadConfig(name: string, directory = path.join(__dirname, '..', 'config')): EnvironmentConfig {
  const file = path.join(directory, `${name}.json`);
  if (!fs.existsSync(file)) {
    throw new Error(`No configuration ${file}; environments are the files in ${directory}.`);
  }

  const config = JSON.parse(fs.readFileSync(file, 'utf8')) as EnvironmentConfig;
  return checkConfig(config);
}

/** Throws on a configuration that cannot be deployed as asked. */
export function checkConfig(config: EnvironmentConfig): EnvironmentConfig {
  const problems: string[] = [];
  if (!/^[a-z][a-z0-9-]{1,20}$/.test(config.envName)) problems.push('envName must be 2-21 lowercase letters, digits or dashes');
  if (config.availabilityZones.length !== 3) problems.push('availabilityZones must name three zones');
  if (config.availabilityZones.some((zone) => !zone.startsWith(config.region))) problems.push(`availabilityZones must be zones of ${config.region}`);
  if (config.server.ephemeralGiB < 21 || config.server.ephemeralGiB > 200) problems.push('server.ephemeralGiB must be 21-200');
  if (config.workers.ephemeralGiB < 21 || config.workers.ephemeralGiB > 200) problems.push('workers.ephemeralGiB must be 21-200');
  if (config.server.minTasks < 1 || config.server.maxTasks < config.server.minTasks) problems.push('server.minTasks must be at least 1 and at most maxTasks');
  if (config.database.minAcu < 0.5 || config.database.maxAcu < config.database.minAcu) problems.push('database.minAcu must be at least 0.5 and at most maxAcu');
  if (config.retainData && !config.domainName) problems.push('an environment that retains its data is production: it needs a domainName (HTTPS)');
  if (config.domainName && !(config.hostedZoneName && config.hostedZoneId) && !config.certificateArn) problems.push('a domainName needs a hostedZoneName and hostedZoneId, or a certificateArn');
  if (config.admins.length === 0) problems.push('admins must name at least one user');
  if (config.webSignIn?.github && !config.githubClientId) problems.push('webSignIn.github needs a githubClientId');
  for (const name of config.webSignIn?.oidc ?? []) {
    if (!config.oidc.some((provider) => provider.name === name)) problems.push(`webSignIn.oidc names '${name}', which is not one of oidc`);
  }
  if (problems.length > 0) {
    throw new Error(`Invalid configuration '${config.envName}': ${problems.join('; ')}.`);
  }

  return config;
}
