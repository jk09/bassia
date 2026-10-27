import * as cdk from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import * as ecs from 'aws-cdk-lib/aws-ecs';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';
import { EnvironmentConfig } from './config';

export interface AgentWorkersProps {
  config: EnvironmentConfig;
  vpc: ec2.IVpc;
  cluster: ecs.ICluster;
  repository: ecr.IRepository;
  /** The server's URL the workers clone from. */
  serverUrl: string;
}

/**
 * Agent workers as ECS Fargate tasks in the server's region (`scripts/run-agent.sh` starts one per run): a task of the
 * worker image logs in with a worker token for its components, clones the server lazily and runs one agent
 * (`deploy/docker/worker-entrypoint.sh`), with ephemeral storage for its working copies and hydrated content. Workers
 * hold no AWS permissions: the server's token is all they have.
 */
export class AgentWorkers extends Construct {
  public readonly taskDefinition: ecs.FargateTaskDefinition;
  public readonly securityGroup: ec2.SecurityGroup;

  constructor(scope: Construct, id: string, props: AgentWorkersProps) {
    super(scope, id);
    const { config, vpc, repository } = props;

    this.securityGroup = new ec2.SecurityGroup(this, 'SecurityGroup', { vpc, description: 'Bassia agent workers: outbound only' });
    const logGroup = new logs.LogGroup(this, 'Logs', {
      logGroupName: `/${config.envName}/workers`,
      retention: logs.RetentionDays.ONE_MONTH,
      removalPolicy: config.retainData ? cdk.RemovalPolicy.RETAIN : cdk.RemovalPolicy.DESTROY,
    });

    this.taskDefinition = new ecs.FargateTaskDefinition(this, 'Task', {
      family: `${config.envName}-worker`,
      cpu: config.workers.cpu,
      memoryLimitMiB: config.workers.memoryMiB,
      ephemeralStorageGiB: config.workers.ephemeralGiB,
      runtimePlatform: { cpuArchitecture: ecs.CpuArchitecture.X86_64, operatingSystemFamily: ecs.OperatingSystemFamily.LINUX },
    });
    const agentSecret = config.workers.agentSecretName
      ? secretsmanager.Secret.fromSecretNameV2(this, 'AgentSecret', config.workers.agentSecretName)
      : undefined;
    this.taskDefinition.addContainer('worker', {
      image: ecs.ContainerImage.fromEcrRepository(repository, config.workers.imageTag),
      environment: { BASSIA_SERVER: props.serverUrl },
      secrets: agentSecret ? { ANTHROPIC_API_KEY: ecs.Secret.fromSecretsManager(agentSecret) } : undefined,
      logging: ecs.LogDrivers.awsLogs({ logGroup, streamPrefix: 'run' }),
    });
  }
}

export interface WorkersStackProps extends cdk.StackProps, AgentWorkersProps {}

/** The agent workers and what `scripts/run-agent.sh` needs to start them. */
export class WorkersStack extends cdk.Stack {
  public readonly workers: AgentWorkers;

  constructor(scope: Construct, id: string, props: WorkersStackProps) {
    super(scope, id, props);
    this.workers = new AgentWorkers(this, 'Workers', props);
    const subnets = props.vpc.selectSubnets({ subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS }).subnetIds;

    new cdk.CfnOutput(this, 'WorkerTaskDefinition', { value: this.workers.taskDefinition.taskDefinitionArn });
    new cdk.CfnOutput(this, 'WorkerSubnets', { value: cdk.Fn.join(',', subnets) });
    new cdk.CfnOutput(this, 'WorkerSecurityGroup', { value: this.workers.securityGroup.securityGroupId });
    new cdk.CfnOutput(this, 'WorkerCapacityProvider', { value: props.config.workers.spot ? 'FARGATE_SPOT' : 'FARGATE' });
  }
}
