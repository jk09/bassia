import * as cdk from 'aws-cdk-lib';
import * as acm from 'aws-cdk-lib/aws-certificatemanager';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as elbv2 from 'aws-cdk-lib/aws-elasticloadbalancingv2';
import * as events from 'aws-cdk-lib/aws-events';
import * as targets from 'aws-cdk-lib/aws-events-targets';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as route53 from 'aws-cdk-lib/aws-route53';
import * as route53Targets from 'aws-cdk-lib/aws-route53-targets';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as wafv2 from 'aws-cdk-lib/aws-wafv2';
import { Construct } from 'constructs';
import { EnvironmentConfig } from './config';
import { DataStack } from './data-stack';

export interface ServerStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  vpc: ec2.IVpc;
  data: DataStack;
}

/**
 * The Bassia server: an ECS Fargate service in the private subnets behind an internet-facing load balancer with TLS
 * and a web application firewall. Every task runs the database migrations first (a container the server waits for;
 * concurrent migrations wait for each other on an advisory lock), so a rolling deployment never serves a schema it does
 * not know. The histories' garbage collection is a scheduled task of the same image.
 */
export class ServerStack extends cdk.Stack {
  public readonly cluster: ecs.Cluster;
  public readonly service: ecs.FargateService;
  public readonly loadBalancer: elbv2.ApplicationLoadBalancer;
  public readonly targetGroup: elbv2.ApplicationTargetGroup;
  public readonly url: string;
  public readonly logGroup: logs.LogGroup;

  constructor(scope: Construct, id: string, props: ServerStackProps) {
    super(scope, id, props);
    const { config, vpc, data } = props;
    const removalPolicy = config.retainData ? cdk.RemovalPolicy.RETAIN : cdk.RemovalPolicy.DESTROY;

    this.cluster = new ecs.Cluster(this, 'Cluster', {
      clusterName: config.envName,
      vpc,
      containerInsightsV2: ecs.ContainerInsights.ENHANCED,
      enableFargateCapacityProviders: true,
    });

    this.logGroup = new logs.LogGroup(this, 'Logs', {
      logGroupName: `/${config.envName}/server`,
      retention: config.retainData ? logs.RetentionDays.THREE_MONTHS : logs.RetentionDays.ONE_WEEK,
      removalPolicy,
    });

    // ----- the load balancer, its certificate and firewall -----

    const zone = config.hostedZoneName && config.hostedZoneId
      ? route53.HostedZone.fromHostedZoneAttributes(this, 'Zone', { hostedZoneId: config.hostedZoneId, zoneName: config.hostedZoneName })
      : undefined;
    const certificate = config.certificateArn
      ? acm.Certificate.fromCertificateArn(this, 'Certificate', config.certificateArn)
      : config.domainName && zone
        ? new acm.Certificate(this, 'Certificate', { domainName: config.domainName, validation: acm.CertificateValidation.fromDns(zone) })
        : undefined;

    const albSecurityGroup = new ec2.SecurityGroup(this, 'LoadBalancerSecurityGroup', { vpc, description: 'Bassia load balancer: HTTPS from anywhere' });
    albSecurityGroup.addIngressRule(ec2.Peer.anyIpv4(), ec2.Port.tcp(443), 'HTTPS');
    albSecurityGroup.addIngressRule(ec2.Peer.anyIpv4(), ec2.Port.tcp(80), 'HTTP, redirected to HTTPS');

    const accessLogs = new s3.Bucket(this, 'AccessLogs', {
      encryption: s3.BucketEncryption.S3_MANAGED,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      enforceSSL: true,
      lifecycleRules: [{ expiration: cdk.Duration.days(90) }],
      removalPolicy,
      autoDeleteObjects: !config.retainData,
    });

    this.loadBalancer = new elbv2.ApplicationLoadBalancer(this, 'LoadBalancer', {
      loadBalancerName: config.envName,
      vpc,
      internetFacing: true,
      vpcSubnets: { subnetType: ec2.SubnetType.PUBLIC },
      securityGroup: albSecurityGroup,
      // Clones and pushes of big histories take minutes.
      idleTimeout: cdk.Duration.minutes(15),
      dropInvalidHeaderFields: true,
      deletionProtection: config.retainData,
    });
    this.loadBalancer.logAccessLogs(accessLogs, 'alb');

    this.targetGroup = new elbv2.ApplicationTargetGroup(this, 'Targets', {
      vpc,
      port: 8080,
      protocol: elbv2.ApplicationProtocol.HTTP,
      targetType: elbv2.TargetType.IP,
      deregistrationDelay: cdk.Duration.seconds(60),
      healthCheck: { path: '/readyz', healthyHttpCodes: '200', interval: cdk.Duration.seconds(15), healthyThresholdCount: 2, unhealthyThresholdCount: 3 },
    });

    if (certificate) {
      this.loadBalancer.addListener('Https', {
        port: 443,
        certificates: [certificate],
        sslPolicy: elbv2.SslPolicy.RECOMMENDED_TLS,
        defaultTargetGroups: [this.targetGroup],
      });
      this.loadBalancer.addRedirect({ sourcePort: 80, targetPort: 443 });
      this.url = `https://${config.domainName}`;
    } else {
      // Staging without a domain: the load balancer's own name, over HTTP.
      this.loadBalancer.addListener('Http', { port: 80, defaultTargetGroups: [this.targetGroup] });
      this.url = `http://${this.loadBalancer.loadBalancerDnsName}`;
    }

    if (zone && config.domainName) {
      new route53.ARecord(this, 'Record', {
        zone,
        recordName: config.domainName,
        target: route53.RecordTarget.fromAlias(new route53Targets.LoadBalancerTarget(this.loadBalancer)),
      });
    }

    const firewall = new wafv2.CfnWebACL(this, 'Firewall', {
      name: config.envName,
      scope: 'REGIONAL',
      defaultAction: { allow: {} },
      visibilityConfig: { cloudWatchMetricsEnabled: true, metricName: config.envName, sampledRequestsEnabled: true },
      rules: [
        {
          name: 'rate-limit',
          priority: 0,
          action: { block: {} },
          statement: { rateBasedStatement: { limit: config.rateLimitPer5Minutes, aggregateKeyType: 'IP' } },
          visibilityConfig: { cloudWatchMetricsEnabled: true, metricName: 'rate-limit', sampledRequestsEnabled: true },
        },
        managedRules('AWSManagedRulesAmazonIpReputationList', 1),
        managedRules('AWSManagedRulesKnownBadInputsRuleSet', 2),
        // Pushes carry packs - binary bodies of any size - which the body rules would take for attacks.
        managedRules('AWSManagedRulesCommonRuleSet', 3, ['SizeRestrictions_BODY', 'CrossSiteScripting_BODY', 'GenericLFI_BODY', 'GenericRFI_BODY']),
      ],
    });
    new wafv2.CfnWebACLAssociation(this, 'FirewallAssociation', { resourceArn: this.loadBalancer.loadBalancerArn, webAclArn: firewall.attrArn });

    // ----- the server's tasks -----

    const serverSecurityGroup = new ec2.SecurityGroup(this, 'ServerSecurityGroup', { vpc, description: 'Bassia server tasks' });
    serverSecurityGroup.addIngressRule(albSecurityGroup, ec2.Port.tcp(8080), 'from the load balancer');
    new ec2.CfnSecurityGroupIngress(this, 'DatabaseFromServer', {
      groupId: data.databaseSecurityGroup.securityGroupId,
      sourceSecurityGroupId: serverSecurityGroup.securityGroupId,
      ipProtocol: 'tcp',
      fromPort: 5432,
      toPort: 5432,
      description: 'PostgreSQL from the Bassia server and its tasks',
    });

    const image = ecs.ContainerImage.fromEcrRepository(data.serverRepository, config.server.imageTag);
    const environment: Record<string, string> = {
      BASSIA_URL: 'http://0.0.0.0:8080',
      BASSIA_STATE: '/scratch',
      BASSIA_PUBLIC_URL: this.url,
      BASSIA_ADMINS: config.admins.join(','),
      BASSIA_DEFAULT_PERMISSION: config.defaultPermission,
      BASSIA_DB_HOST: data.database.clusterEndpoint.hostname,
      BASSIA_DB_PORT: cdk.Token.asString(data.database.clusterEndpoint.port),
      BASSIA_DB_NAME: 'bassia',
      BASSIA_BUCKET: data.bucket.bucketName,
      BASSIA_TABLE: data.table.tableName,
      BASSIA_REGION: config.region,
      ...(config.organization ? { BASSIA_ORGANIZATION: config.organization } : {}),
      ...(config.oidc.length > 0 ? { BASSIA_OIDC: JSON.stringify(config.oidc) } : {}),
      ...(config.githubClientId ? { BASSIA_GITHUB_CLIENT_ID: config.githubClientId } : {}),
    };
    const databaseSecret = data.database.secret!;
    const secrets: Record<string, ecs.Secret> = {
      BASSIA_DB_USER: ecs.Secret.fromSecretsManager(databaseSecret, 'username'),
      BASSIA_DB_PASSWORD: ecs.Secret.fromSecretsManager(databaseSecret, 'password'),
      BASSIA_SERVER_SIGNING_KEY: ecs.Secret.fromSecretsManager(data.signingKey),
    };

    // What the server and its tasks may do with the histories, and nothing else.
    const taskRole = (name: string) => {
      const role = new iam.Role(this, `${name}Role`, { assumedBy: new iam.ServicePrincipal('ecs-tasks.amazonaws.com'), description: `Bassia ${name.toLowerCase()} tasks` });
      data.bucket.grantReadWrite(role);
      data.bucket.grantDelete(role);
      data.table.grantReadWriteData(role);
      role.addToPolicy(new iam.PolicyStatement({ actions: ['s3:GetBucketVersioning'], resources: [data.bucket.bucketArn] }));
      role.addToPolicy(new iam.PolicyStatement({ actions: ['dynamodb:DescribeContinuousBackups'], resources: [data.table.tableArn] }));
      return role;
    };

    const task = new ecs.FargateTaskDefinition(this, 'ServerTask', {
      family: `${config.envName}-server`,
      cpu: config.server.cpu,
      memoryLimitMiB: config.server.memoryMiB,
      ephemeralStorageGiB: config.server.ephemeralGiB,
      runtimePlatform: { cpuArchitecture: ecs.CpuArchitecture.X86_64, operatingSystemFamily: ecs.OperatingSystemFamily.LINUX },
      taskRole: taskRole('Server'),
    });
    const migrate = task.addContainer('migrate', {
      image,
      command: ['migrate'],
      essential: false,
      environment,
      secrets,
      logging: ecs.LogDrivers.awsLogs({ logGroup: this.logGroup, streamPrefix: 'migrate' }),
    });
    const server = task.addContainer('server', {
      image,
      essential: true,
      environment,
      secrets,
      portMappings: [{ containerPort: 8080, protocol: ecs.Protocol.TCP }],
      logging: ecs.LogDrivers.awsLogs({ logGroup: this.logGroup, streamPrefix: 'server' }),
      ulimits: [{ name: ecs.UlimitName.NOFILE, softLimit: 65536, hardLimit: 65536 }],
      stopTimeout: cdk.Duration.seconds(60),
    });
    server.addContainerDependencies({ container: migrate, condition: ecs.ContainerDependencyCondition.SUCCESS });

    this.service = new ecs.FargateService(this, 'Service', {
      serviceName: `${config.envName}-server`,
      cluster: this.cluster,
      taskDefinition: task,
      desiredCount: config.server.minTasks,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      securityGroups: [serverSecurityGroup],
      assignPublicIp: false,
      // A rolling deployment that never drops below the desired count, rolled back when new tasks do not get healthy.
      minHealthyPercent: 100,
      maxHealthyPercent: 200,
      circuitBreaker: { enable: true, rollback: true },
      healthCheckGracePeriod: cdk.Duration.minutes(3),
      propagateTags: ecs.PropagatedTagSource.SERVICE,
    });
    this.service.attachToApplicationTargetGroup(this.targetGroup);

    const scaling = this.service.autoScaleTaskCount({ minCapacity: config.server.minTasks, maxCapacity: config.server.maxTasks });
    scaling.scaleOnCpuUtilization('Cpu', { targetUtilizationPercent: 60, scaleInCooldown: cdk.Duration.minutes(5), scaleOutCooldown: cdk.Duration.minutes(1) });
    scaling.scaleOnRequestCount('Requests', { requestsPerTarget: 3000, targetGroup: this.targetGroup, scaleInCooldown: cdk.Duration.minutes(5), scaleOutCooldown: cdk.Duration.minutes(1) });

    // ----- garbage collection, on a schedule -----

    const gc = new ecs.FargateTaskDefinition(this, 'GcTask', {
      family: `${config.envName}-gc`,
      cpu: 1024,
      memoryLimitMiB: 4096,
      ephemeralStorageGiB: config.server.ephemeralGiB,
      runtimePlatform: { cpuArchitecture: ecs.CpuArchitecture.X86_64, operatingSystemFamily: ecs.OperatingSystemFamily.LINUX },
      taskRole: taskRole('Gc'),
    });
    // The restore drill (scripts/restore-drill.sh) runs this task on restored copies named after the live ones.
    gc.taskRole.addToPrincipalPolicy(new iam.PolicyStatement({
      actions: ['s3:GetObject*', 's3:PutObject', 's3:DeleteObject*', 's3:ListBucket', 's3:GetBucketVersioning'],
      resources: [`arn:aws:s3:::${config.envName}-histories-${this.account}-drill-*`, `arn:aws:s3:::${config.envName}-histories-${this.account}-drill-*/*`],
    }));
    gc.taskRole.addToPrincipalPolicy(new iam.PolicyStatement({
      actions: ['dynamodb:GetItem', 'dynamodb:Query', 'dynamodb:PutItem', 'dynamodb:UpdateItem', 'dynamodb:DeleteItem', 'dynamodb:ConditionCheckItem', 'dynamodb:DescribeContinuousBackups'],
      resources: [`arn:aws:dynamodb:${this.region}:${this.account}:table/${config.envName}-drill-*`],
    }));
    gc.addContainer('gc', {
      image,
      command: ['gc'],
      environment,
      secrets,
      logging: ecs.LogDrivers.awsLogs({ logGroup: this.logGroup, streamPrefix: 'gc' }),
    });
    new events.Rule(this, 'GcSchedule', {
      description: 'Bassia: collect the garbage of the histories',
      schedule: events.Schedule.expression(config.gcSchedule),
      targets: [new targets.EcsTask({
        cluster: this.cluster,
        taskDefinition: gc,
        subnetSelection: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
        securityGroups: [serverSecurityGroup],
        launchType: ecs.LaunchType.FARGATE,
      })],
    });

    new cdk.CfnOutput(this, 'Url', { value: this.url, description: 'Where users reach the server (bassia login -server <url>)' });
    new cdk.CfnOutput(this, 'ClusterName', { value: this.cluster.clusterName });
    new cdk.CfnOutput(this, 'ServiceName', { value: this.service.serviceName });
    // For one-off tasks of the server image (scripts/staging-check.sh, restore-drill.sh): the gc task's definition, run
    // with another command, in the server's subnets and security group.
    new cdk.CfnOutput(this, 'TaskDefinition', { value: gc.taskDefinitionArn });
    new cdk.CfnOutput(this, 'TaskSubnets', { value: cdk.Fn.join(',', vpc.selectSubnets({ subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS }).subnetIds) });
    new cdk.CfnOutput(this, 'TaskSecurityGroup', { value: serverSecurityGroup.securityGroupId });
    new cdk.CfnOutput(this, 'LogGroup', { value: this.logGroup.logGroupName });
  }
}

/** An AWS managed rule group, the named rules of it only counted. */
function managedRules(name: string, priority: number, countOnly: string[] = []): wafv2.CfnWebACL.RuleProperty {
  return {
    name,
    priority,
    overrideAction: { none: {} },
    statement: {
      managedRuleGroupStatement: {
        vendorName: 'AWS',
        name,
        ruleActionOverrides: countOnly.map((rule) => ({ name: rule, actionToUse: { count: {} } })),
      },
    },
    visibilityConfig: { cloudWatchMetricsEnabled: true, metricName: name, sampledRequestsEnabled: true },
  };
}
