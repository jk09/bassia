import * as cdk from 'aws-cdk-lib';
import * as backup from 'aws-cdk-lib/aws-backup';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import * as ecr from 'aws-cdk-lib/aws-ecr';
import * as events from 'aws-cdk-lib/aws-events';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as kms from 'aws-cdk-lib/aws-kms';
import * as rds from 'aws-cdk-lib/aws-rds';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as secretsmanager from 'aws-cdk-lib/aws-secretsmanager';
import { Construct } from 'constructs';
import { EnvironmentConfig } from './config';

export interface DataStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  vpc: ec2.IVpc;
}

/**
 * Everything the server keeps: the histories' bucket and table (bassia-ce722c6c), the Aurora PostgreSQL database
 * (bassia-f245cd5f), the secrets, the image repositories, one KMS key for all of it, and an AWS Backup plan over the
 * database, the table and the bucket. In production all of it outlives the stacks.
 */
export class DataStack extends cdk.Stack {
  public readonly key: kms.Key;
  public readonly bucket: s3.Bucket;
  public readonly table: dynamodb.Table;
  public readonly database: rds.DatabaseCluster;
  public readonly databaseSecurityGroup: ec2.SecurityGroup;
  public readonly signingKey: secretsmanager.Secret;
  public readonly serverRepository: ecr.Repository;
  public readonly workerRepository: ecr.Repository;

  constructor(scope: Construct, id: string, props: DataStackProps) {
    super(scope, id, props);
    const { config, vpc } = props;
    const removalPolicy = config.retainData ? cdk.RemovalPolicy.RETAIN : cdk.RemovalPolicy.DESTROY;

    this.key = new kms.Key(this, 'Key', {
      alias: `alias/${config.envName}`,
      description: `Bassia (${config.envName}): histories, records and backups`,
      enableKeyRotation: true,
      removalPolicy,
    });

    // Packs are written once and never changed; old versions are what a restore goes back to (see docs/deploy-aws.md).
    this.bucket = new s3.Bucket(this, 'Histories', {
      bucketName: `${config.envName}-histories-${this.account}`,
      versioned: true,
      encryption: s3.BucketEncryption.KMS,
      encryptionKey: this.key,
      bucketKeyEnabled: true,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      enforceSSL: true,
      objectOwnership: s3.ObjectOwnership.BUCKET_OWNER_ENFORCED,
      removalPolicy,
      autoDeleteObjects: !config.retainData,
      lifecycleRules: [
        { id: 'cheaper-tiers', transitions: [{ storageClass: s3.StorageClass.INTELLIGENT_TIERING, transitionAfter: cdk.Duration.days(30) }] },
        { id: 'old-versions', noncurrentVersionExpiration: cdk.Duration.days(35), abortIncompleteMultipartUploadAfter: cdk.Duration.days(2) },
      ],
    });

    // The single table of the cloud storage: partition pk, sort sk (both strings).
    this.table = new dynamodb.Table(this, 'Records', {
      tableName: config.envName,
      partitionKey: { name: 'pk', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'sk', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      pointInTimeRecoverySpecification: { pointInTimeRecoveryEnabled: true },
      encryption: dynamodb.TableEncryption.CUSTOMER_MANAGED,
      encryptionKey: this.key,
      deletionProtection: config.retainData,
      removalPolicy,
    });

    this.databaseSecurityGroup = new ec2.SecurityGroup(this, 'DatabaseSecurityGroup', {
      vpc,
      description: 'Bassia database: PostgreSQL from the server and its tasks only',
      allowAllOutbound: false,
    });

    this.database = new rds.DatabaseCluster(this, 'Database', {
      engine: rds.DatabaseClusterEngine.auroraPostgres({
        version: rds.AuroraPostgresEngineVersion.of(config.database.engineVersion, config.database.engineVersion.split('.')[0]),
      }),
      defaultDatabaseName: 'bassia',
      // Secrets use Secrets Manager's own key: one of the data key would put the server's roles into its key policy.
      credentials: rds.Credentials.fromGeneratedSecret('bassia', { secretName: `${config.envName}/database` }),
      writer: rds.ClusterInstance.serverlessV2('writer', { publiclyAccessible: false }),
      readers: [rds.ClusterInstance.serverlessV2('reader', { scaleWithWriter: true, publiclyAccessible: false })],
      serverlessV2MinCapacity: config.database.minAcu,
      serverlessV2MaxCapacity: config.database.maxAcu,
      vpc,
      vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_ISOLATED },
      securityGroups: [this.databaseSecurityGroup],
      storageEncrypted: true,
      storageEncryptionKey: this.key,
      backup: { retention: cdk.Duration.days(config.database.backupDays), preferredWindow: '01:00-02:00' },
      deletionProtection: config.retainData,
      removalPolicy: config.retainData ? cdk.RemovalPolicy.SNAPSHOT : cdk.RemovalPolicy.DESTROY,
      cloudwatchLogsExports: ['postgresql'],
      iamAuthentication: false,
      parameters: { 'rds.force_ssl': '1' },
    });

    this.signingKey = new secretsmanager.Secret(this, 'SigningKey', {
      secretName: `${config.envName}/signing-key`,
      description: 'Signs the Bassia server\'s own tokens (BASSIA_SERVER_SIGNING_KEY)',
      generateSecretString: { passwordLength: 64, excludePunctuation: true },
      removalPolicy,
    });

    const repository = (name: string) => new ecr.Repository(this, `${name}Repository`, {
      repositoryName: `${config.envName}/${name.toLowerCase()}`,
      imageScanOnPush: true,
      // A version tag is published once; staging reuses its tag.
      imageTagMutability: config.retainData ? ecr.TagMutability.IMMUTABLE : ecr.TagMutability.MUTABLE,
      encryption: ecr.RepositoryEncryption.KMS,
      encryptionKey: this.key,
      lifecycleRules: [{ description: 'keep the last 30 images', maxImageCount: 30 }],
      removalPolicy,
      emptyOnDelete: !config.retainData,
    });
    this.serverRepository = repository('Server');
    this.workerRepository = repository('Worker');

    // Daily backups of all three, kept 35 days, beside their own point-in-time recovery - for data that outlives the
    // stacks: a vault holding recovery points cannot be deleted, so staging relies on point-in-time recovery alone.
    if (config.retainData) {
      const plan = new backup.BackupPlan(this, 'Backups', {
        backupPlanName: config.envName,
        backupVault: new backup.BackupVault(this, 'Vault', { backupVaultName: config.envName, encryptionKey: this.key, removalPolicy }),
        backupPlanRules: [
          new backup.BackupPlanRule({
            ruleName: 'daily',
            scheduleExpression: events.Schedule.cron({ hour: '2', minute: '30' }),
            deleteAfter: cdk.Duration.days(35),
            enableContinuousBackup: false,
          }),
        ],
      });
      const backupRole = new iam.Role(this, 'BackupRole', {
        assumedBy: new iam.ServicePrincipal('backup.amazonaws.com'),
        managedPolicies: [
          iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AWSBackupServiceRolePolicyForBackup'),
          iam.ManagedPolicy.fromAwsManagedPolicyName('service-role/AWSBackupServiceRolePolicyForRestores'),
          iam.ManagedPolicy.fromAwsManagedPolicyName('AWSBackupServiceRolePolicyForS3Backup'),
          iam.ManagedPolicy.fromAwsManagedPolicyName('AWSBackupServiceRolePolicyForS3Restore'),
        ],
      });
      this.key.grantEncryptDecrypt(backupRole);
      plan.addSelection('Everything', {
        role: backupRole,
        resources: [
          backup.BackupResource.fromRdsDatabaseCluster(this.database),
          backup.BackupResource.fromDynamoDbTable(this.table),
          backup.BackupResource.fromArn(this.bucket.bucketArn),
        ],
      });
    }

    new cdk.CfnOutput(this, 'BucketName', { value: this.bucket.bucketName });
    new cdk.CfnOutput(this, 'TableName', { value: this.table.tableName });
    new cdk.CfnOutput(this, 'ServerRepositoryUri', { value: this.serverRepository.repositoryUri });
    new cdk.CfnOutput(this, 'WorkerRepositoryUri', { value: this.workerRepository.repositoryUri });
  }
}
