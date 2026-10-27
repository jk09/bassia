import * as cdk from 'aws-cdk-lib';
import { Match, Template } from 'aws-cdk-lib/assertions';
import { addEnvironment, BassiaStacks } from '../lib/app';
import { checkConfig, EnvironmentConfig, loadConfig } from '../lib/config';

function synthesize(name: string, change: (config: EnvironmentConfig) => void = () => {}) {
  const config = loadConfig(name);
  change(config);
  config.account = '123456789012';
  const stacks = addEnvironment(new cdk.App(), checkConfig(config));
  const templates = Object.fromEntries(Object.entries(stacks).map(([key, stack]) => [key, Template.fromStack(stack)])) as Record<keyof BassiaStacks, Template>;
  return { config, stacks, templates };
}

const production = synthesize('production');
const staging = synthesize('staging');

describe('network', () => {
  test('spans three availability zones with private compute and isolated data subnets', () => {
    const network = production.templates.network;
    const zones = new Set(Object.values(network.findResources('AWS::EC2::Subnet')).map((subnet) => subnet.Properties.AvailabilityZone));
    expect([...zones].sort()).toEqual(['eu-central-1a', 'eu-central-1b', 'eu-central-1c']);
    network.resourceCountIs('AWS::EC2::Subnet', 9);
    network.resourceCountIs('AWS::EC2::NatGateway', 3);
    staging.templates.network.resourceCountIs('AWS::EC2::NatGateway', 1);
  });

  test('reaches S3 and DynamoDB through gateway endpoints', () => {
    const endpoints = Object.values(production.templates.network.findResources('AWS::EC2::VPCEndpoint'));
    expect(endpoints.map((endpoint) => endpoint.Properties.VpcEndpointType ?? 'Gateway')).toEqual(['Gateway', 'Gateway']);
    expect(JSON.stringify(endpoints)).toContain('.s3');
    expect(JSON.stringify(endpoints)).toContain('.dynamodb');
  });
});

describe('data', () => {
  const data = production.templates.data;

  test('the histories bucket is versioned, KMS encrypted, private, and moves old objects to cheaper tiers', () => {
    data.hasResourceProperties('AWS::S3::Bucket', {
      VersioningConfiguration: { Status: 'Enabled' },
      BucketEncryption: { ServerSideEncryptionConfiguration: [Match.objectLike({ ServerSideEncryptionByDefault: Match.objectLike({ SSEAlgorithm: 'aws:kms' }) })] },
      PublicAccessBlockConfiguration: { BlockPublicAcls: true, BlockPublicPolicy: true, IgnorePublicAcls: true, RestrictPublicBuckets: true },
      LifecycleConfiguration: { Rules: Match.arrayWith([Match.objectLike({ Transitions: [{ StorageClass: 'INTELLIGENT_TIERING', TransitionInDays: 30 }] })]) },
    });
    data.hasResource('AWS::S3::Bucket', { DeletionPolicy: 'Retain' });
  });

  test('the table is on demand, recoverable to a point in time, and KMS encrypted', () => {
    data.hasResourceProperties('AWS::DynamoDB::Table', {
      KeySchema: [{ AttributeName: 'pk', KeyType: 'HASH' }, { AttributeName: 'sk', KeyType: 'RANGE' }],
      BillingMode: 'PAY_PER_REQUEST',
      PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
      SSESpecification: Match.objectLike({ SSEEnabled: true, SSEType: 'KMS' }),
      DeletionProtectionEnabled: true,
    });
  });

  test('Aurora PostgreSQL is encrypted, private, backed up, in two zones', () => {
    data.hasResourceProperties('AWS::RDS::DBCluster', {
      Engine: 'aurora-postgresql',
      StorageEncrypted: true,
      BackupRetentionPeriod: 14,
      DeletionProtection: true,
      ServerlessV2ScalingConfiguration: { MinCapacity: 0.5, MaxCapacity: 16 },
    });
    const instances = Object.values(data.findResources('AWS::RDS::DBInstance'));
    expect(instances).toHaveLength(2);
    instances.forEach((instance) => expect(instance.Properties.PubliclyAccessible).toBe(false));
  });

  test('an AWS Backup plan covers the database, the table and the bucket', () => {
    data.resourceCountIs('AWS::Backup::BackupPlan', 1);
    const selection = JSON.stringify(data.findResources('AWS::Backup::BackupSelection'));
    for (const resource of [':cluster:', 'Records', 'Histories']) {
      expect(selection).toContain(resource);
    }
  });

  test('staging data goes with its stacks', () => {
    staging.templates.data.hasResource('AWS::S3::Bucket', { DeletionPolicy: 'Delete' });
    staging.templates.data.resourceCountIs('AWS::Backup::BackupPlan', 0);
    staging.templates.data.hasResourceProperties('AWS::DynamoDB::Table', { DeletionProtectionEnabled: false });
  });
});

describe('server', () => {
  const server = production.templates.server;

  test('tasks have ephemeral storage for the git cache, and run the migrations before the server', () => {
    server.hasResourceProperties('AWS::ECS::TaskDefinition', {
      Family: 'bassia-server',
      EphemeralStorage: { SizeInGiB: 100 },
      ContainerDefinitions: Match.arrayWith([
        Match.objectLike({ Name: 'migrate', Command: ['migrate'], Essential: false }),
        Match.objectLike({ Name: 'server', DependsOn: [{ Condition: 'SUCCESS', ContainerName: 'migrate' }], PortMappings: [Match.objectLike({ ContainerPort: 8080 })] }),
      ]),
    });
  });

  test('the configuration comes from the environment, the secrets from Secrets Manager', () => {
    const task = Object.values(server.findResources('AWS::ECS::TaskDefinition')).find((resource) => resource.Properties.Family === 'bassia-server')!;
    const container = task.Properties.ContainerDefinitions.find((definition: { Name: string }) => definition.Name === 'server');
    const names = container.Environment.map((variable: { Name: string }) => variable.Name);
    expect(names).toEqual(expect.arrayContaining(['BASSIA_DB_HOST', 'BASSIA_BUCKET', 'BASSIA_TABLE', 'BASSIA_REGION', 'BASSIA_ADMINS', 'BASSIA_OIDC', 'BASSIA_PUBLIC_URL']));
    expect(container.Secrets.map((secret: { Name: string }) => secret.Name).sort()).toEqual(['BASSIA_DB_PASSWORD', 'BASSIA_DB_USER', 'BASSIA_SERVER_SIGNING_KEY']);
    expect(JSON.stringify(container.Environment)).not.toContain('PASSWORD');
  });

  test('a rolling deployment keeps the desired count and rolls back on failure', () => {
    server.hasResourceProperties('AWS::ECS::Service', {
      LaunchType: 'FARGATE',
      DesiredCount: 2,
      DeploymentConfiguration: Match.objectLike({
        MinimumHealthyPercent: 100,
        MaximumPercent: 200,
        DeploymentCircuitBreaker: { Enable: true, Rollback: true },
      }),
      NetworkConfiguration: { AwsvpcConfiguration: Match.objectLike({ AssignPublicIp: 'DISABLED' }) },
    });
  });

  test('scales on CPU and requests within the configured bounds', () => {
    server.hasResourceProperties('AWS::ApplicationAutoScaling::ScalableTarget', { MinCapacity: 2, MaxCapacity: 8 });
    const policies = Object.values(server.findResources('AWS::ApplicationAutoScaling::ScalingPolicy'))
      .map((policy) => policy.Properties.TargetTrackingScalingPolicyConfiguration.PredefinedMetricSpecification.PredefinedMetricType).sort();
    expect(policies).toEqual(['ALBRequestCountPerTarget', 'ECSServiceAverageCPUUtilization']);
  });

  test('serves HTTPS with a validated certificate, redirects HTTP, and is behind a web application firewall', () => {
    server.hasResourceProperties('AWS::ElasticLoadBalancingV2::Listener', { Port: 443, Protocol: 'HTTPS' });
    server.hasResourceProperties('AWS::ElasticLoadBalancingV2::Listener', { Port: 80, DefaultActions: [Match.objectLike({ Type: 'redirect' })] });
    server.hasResourceProperties('AWS::CertificateManager::Certificate', { DomainName: 'bassia.example.com', ValidationMethod: 'DNS' });
    server.resourceCountIs('AWS::WAFv2::WebACLAssociation', 1);
    server.hasResourceProperties('AWS::WAFv2::WebACL', {
      Scope: 'REGIONAL',
      Rules: Match.arrayWith([Match.objectLike({ Name: 'rate-limit', Statement: { RateBasedStatement: { Limit: 20000, AggregateKeyType: 'IP' } } })]),
    });
    server.hasResourceProperties('AWS::ElasticLoadBalancingV2::TargetGroup', { HealthCheckPath: '/readyz', TargetType: 'ip' });
  });

  test('the server\'s role reaches its bucket and table only', () => {
    const policies = JSON.stringify(server.findResources('AWS::IAM::Policy'));
    expect(policies).not.toMatch(/"Action":"(s3|dynamodb):\*"/);
    expect(policies).not.toMatch(/"Resource":"\*"[^}]*"(s3|dynamodb):/);
    // A transaction is authorized by the item-level actions it is made of.
    for (const action of ['dynamodb:UpdateItem', 'dynamodb:DeleteItem', 'dynamodb:ConditionCheckItem', 'dynamodb:Query', 's3:PutObject', 's3:DeleteObject*']) {
      expect(policies).toContain(action);
    }
  });

  test('metrics go over OTLP to a collector beside the server, and on to CloudWatch with the dimensions the alarms use', () => {
    const task = Object.values(server.findResources('AWS::ECS::TaskDefinition')).find((resource) => resource.Properties.Family === 'bassia-server')!;
    const containers = task.Properties.ContainerDefinitions as { Name: string; Essential: boolean; Image: string; Environment: { Name: string; Value: string }[] }[];
    const collector = containers.find((definition) => definition.Name === 'otel-collector')!;
    expect(collector.Essential).toBe(false);
    expect(collector.Image).toMatch(/aws-otel-collector:v\d+\.\d+\.\d+$/);
    const settings = JSON.parse(collector.Environment.find((variable) => variable.Name === 'AOT_CONFIG_CONTENT')!.Value);
    expect(settings.exporters.awsemf.namespace).toBe('Bassia');
    expect(settings.exporters.awsemf.metric_declarations).toContainEqual({ dimensions: [['result']], metric_name_selectors: ['^bassia\\.lock\\.decisions$'] });
    const variables = Object.fromEntries(containers.find((definition) => definition.Name === 'server')!.Environment.map((variable) => [variable.Name, variable.Value]));
    expect(variables.OTEL_EXPORTER_OTLP_ENDPOINT).toBe('http://127.0.0.1:4318');
    expect(JSON.stringify(server.findResources('AWS::IAM::Policy'))).toContain('logs:PutLogEvents');
    server.hasResourceProperties('AWS::Logs::LogGroup', { LogGroupName: '/bassia/metrics' });
  });

  test('the dashboard\'s sign-in gets its client secrets from the configured secret', () => {
    const signedIn = synthesize('production', (config) => {
      config.githubClientId = 'gh-app';
      config.webSignIn = { secretName: 'bassia/web-sign-in', github: true, oidc: [config.oidc[0].name] };
    });
    const task = Object.values(signedIn.templates.server.findResources('AWS::ECS::TaskDefinition')).find((resource) => resource.Properties.Family === 'bassia-server')!;
    const container = task.Properties.ContainerDefinitions.find((definition: { Name: string }) => definition.Name === 'server');
    expect(container.Secrets.map((secret: { Name: string }) => secret.Name)).toEqual(expect.arrayContaining([
      'BASSIA_GITHUB_CLIENT_SECRET', `BASSIA_OIDC_${signedIn.config.oidc[0].name.toUpperCase()}_CLIENT_SECRET`]));
    expect(() => synthesize('staging', (config) => { config.webSignIn = { secretName: 'x', github: true, oidc: [] }; })).toThrow('needs a githubClientId');
    expect(() => synthesize('staging', (config) => { config.webSignIn = { secretName: 'x', github: false, oidc: ['nobody'] }; })).toThrow('not one of oidc');
  });

  test('the database admits the server tasks only', () => {
    server.hasResourceProperties('AWS::EC2::SecurityGroupIngress', { FromPort: 5432, ToPort: 5432, IpProtocol: 'tcp' });
  });

  test('collects the histories\' garbage on a schedule, as a task of its own', () => {
    server.hasResourceProperties('AWS::ECS::TaskDefinition', {
      Family: 'bassia-gc',
      ContainerDefinitions: [Match.objectLike({ Command: ['gc'] })],
    });
    server.hasResourceProperties('AWS::Events::Rule', { ScheduleExpression: 'cron(17 3 * * ? *)' });
  });

  test('staging without a domain listens on HTTP', () => {
    staging.templates.server.hasResourceProperties('AWS::ElasticLoadBalancingV2::Listener', { Port: 80, Protocol: 'HTTP', DefaultActions: [Match.objectLike({ Type: 'forward' })] });
    staging.templates.server.resourceCountIs('AWS::CertificateManager::Certificate', 0);
  });
});

describe('workers', () => {
  test('run on Fargate with ephemeral storage and no AWS permissions', () => {
    const workers = production.templates.workers;
    workers.hasResourceProperties('AWS::ECS::TaskDefinition', {
      Family: 'bassia-worker',
      EphemeralStorage: { SizeInGiB: 60 },
      ContainerDefinitions: [Match.objectLike({ Environment: [{ Name: 'BASSIA_SERVER', Value: 'https://bassia.example.com' }] })],
    });
    // The task role CDK gives every task has no policy: workers reach nothing in AWS.
    workers.resourceCountIs('AWS::IAM::Role', 2);
    expect(JSON.stringify(workers.findResources('AWS::IAM::Policy'))).not.toContain('TaskTaskRole');
    workers.hasOutput('WorkerCapacityProvider', { Value: 'FARGATE_SPOT' });
  });
});

describe('observability', () => {
  const observability = production.templates.observability;

  test('alarms on errors, latency, health, scaling, the database, the table, storage, lock conflicts and the merge queue, to email', () => {
    const names = Object.values(observability.findResources('AWS::CloudWatch::Alarm')).map((alarm) => alarm.Properties.AlarmName).sort();
    expect(names).toEqual([
      'bassia-database-capacity', 'bassia-database-cpu', 'bassia-latency', 'bassia-lock-conflicts', 'bassia-merge-queue', 'bassia-scaling-at-maximum',
      'bassia-server-cpu', 'bassia-server-errors', 'bassia-storage-growth', 'bassia-table-errors', 'bassia-table-throttling', 'bassia-unhealthy-tasks',
    ]);
    const metric = (properties: object) => [Match.objectLike({ MetricStat: Match.objectLike({ Metric: properties }) })];
    observability.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'bassia-lock-conflicts', Threshold: 50, Metrics: metric({ Namespace: 'Bassia', MetricName: 'bassia.lock.conflicts' }),
    });
    observability.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'bassia-merge-queue', Threshold: 20,
      Metrics: metric({ Namespace: 'Bassia', MetricName: 'bassia.merge_queue.depth', Dimensions: [{ Name: 'state', Value: 'waiting' }] }),
    });
    observability.hasResourceProperties('AWS::SNS::Subscription', { Protocol: 'email', Endpoint: 'ops@example.com' });
    observability.resourceCountIs('AWS::CloudWatch::Dashboard', 1);
  });

  test('a monthly budget of $1,500 with alerts', () => {
    observability.hasResourceProperties('AWS::Budgets::Budget', {
      Budget: { BudgetLimit: { Amount: 1500, Unit: 'USD' }, BudgetType: 'COST', TimeUnit: 'MONTHLY', BudgetName: 'bassia' },
      NotificationsWithSubscribers: Match.arrayWith([Match.objectLike({ Notification: Match.objectLike({ NotificationType: 'FORECASTED', Threshold: 100 }) })]),
    });
  });
});

describe('configuration', () => {
  test('refuses what cannot be deployed as asked', () => {
    expect(() => synthesize('production', (config) => { config.domainName = null; })).toThrow('needs a domainName');
    expect(() => synthesize('staging', (config) => { config.availabilityZones = ['eu-central-1a']; })).toThrow('three zones');
    expect(() => synthesize('staging', (config) => { config.server.ephemeralGiB = 500; })).toThrow('21-200');
  });

  test('another region is another configuration', () => {
    const elsewhere = synthesize('staging', (config) => {
      config.region = 'eu-west-1';
      config.availabilityZones = ['eu-west-1a', 'eu-west-1b', 'eu-west-1c'];
    });
    expect(elsewhere.stacks.server.region).toBe('eu-west-1');
  });
});
