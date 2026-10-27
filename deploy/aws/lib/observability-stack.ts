import * as cdk from 'aws-cdk-lib';
import * as budgets from 'aws-cdk-lib/aws-budgets';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as actions from 'aws-cdk-lib/aws-cloudwatch-actions';
import * as sns from 'aws-cdk-lib/aws-sns';
import * as subscriptions from 'aws-cdk-lib/aws-sns-subscriptions';
import { Construct } from 'constructs';
import { EnvironmentConfig } from './config';
import { DataStack } from './data-stack';
import { ServerStack } from './server-stack';

export interface ObservabilityStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
  data: DataStack;
  server: ServerStack;
}

/**
 * What an operator watches: a dashboard of the load balancer, the service, the database, the table and the bucket;
 * alarms for errors, latency, unhealthy tasks, scaling at its maximum, database load, table throttling and storage
 * growth, sent to an SNS topic with an email subscription; and the monthly budget with alerts.
 */
export class ObservabilityStack extends cdk.Stack {
  public readonly topic: sns.Topic;
  public readonly alarms: cloudwatch.Alarm[] = [];

  constructor(scope: Construct, id: string, props: ObservabilityStackProps) {
    super(scope, id, props);
    const { config, data, server } = props;

    this.topic = new sns.Topic(this, 'Alarms', { topicName: `${config.envName}-alarms`, enforceSSL: true });
    this.topic.addSubscription(new subscriptions.EmailSubscription(config.alarmEmail));

    const alb = server.loadBalancer.metrics;
    const targets = server.targetGroup.metrics;
    const period = cdk.Duration.minutes(5);
    const errors = alb.httpCodeTarget(cdk.aws_elasticloadbalancingv2.HttpCodeTarget.TARGET_5XX_COUNT, { period, statistic: 'Sum' });
    const latency = alb.targetResponseTime({ period, statistic: 'p99' });
    const requests = alb.requestCount({ period, statistic: 'Sum' });
    const unhealthy = targets.unhealthyHostCount({ period, statistic: 'Maximum' });
    const cpu = server.service.metricCpuUtilization({ period });
    const memory = server.service.metricMemoryUtilization({ period });
    const running = new cloudwatch.Metric({
      namespace: 'ECS/ContainerInsights',
      metricName: 'RunningTaskCount',
      dimensionsMap: { ClusterName: server.cluster.clusterName, ServiceName: server.service.serviceName },
      period,
      statistic: 'Maximum',
    });
    const databaseCapacity = data.database.metricServerlessDatabaseCapacity({ period, statistic: 'Maximum' });
    const databaseCpu = data.database.metricCPUUtilization({ period });
    const connections = data.database.metricDatabaseConnections({ period });
    const throttled = new cloudwatch.MathExpression({
      expression: 'reads + writes',
      usingMetrics: {
        reads: data.table.metric('ReadThrottleEvents', { period, statistic: 'Sum' }),
        writes: data.table.metric('WriteThrottleEvents', { period, statistic: 'Sum' }),
      },
      period,
      label: 'Throttled requests',
    });
    // The operations the cloud storage uses (bassia-ce722c6c): consistent reads, queries, transactions.
    const systemErrors = data.table.metricSystemErrorsForOperations({
      period,
      operations: [cdk.aws_dynamodb.Operation.GET_ITEM, cdk.aws_dynamodb.Operation.QUERY, cdk.aws_dynamodb.Operation.TRANSACT_WRITE_ITEMS],
    });
    const bucketSize = new cloudwatch.Metric({
      namespace: 'AWS/S3',
      metricName: 'BucketSizeBytes',
      dimensionsMap: { BucketName: data.bucket.bucketName, StorageType: 'StandardStorage' },
      period: cdk.Duration.days(1),
      statistic: 'Average',
    });

    const alarm = (name: string, metric: cloudwatch.IMetric, threshold: number, description: string, evaluationPeriods = 3,
      comparison = cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD) => {
      const created = new cloudwatch.Alarm(this, name, {
        alarmName: `${config.envName}-${name}`,
        alarmDescription: description,
        metric,
        threshold,
        evaluationPeriods,
        comparisonOperator: comparison,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });
      created.addAlarmAction(new actions.SnsAction(this.topic));
      created.addOkAction(new actions.SnsAction(this.topic));
      this.alarms.push(created);
      return created;
    };

    alarm('server-errors', errors, 25, 'The server answered more than 25 errors (5xx) in 5 minutes, three times in a row.');
    alarm('latency', latency, 10, 'The 99th percentile of the server\'s response time is above 10 seconds.');
    alarm('unhealthy-tasks', unhealthy, 0, 'A server task failed the load balancer\'s health check (/readyz) for 10 minutes.', 2);
    alarm('scaling-at-maximum', running, config.server.maxTasks - 1, `The server runs its maximum of ${config.server.maxTasks} tasks: it cannot scale further.`, 3,
      cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD);
    alarm('server-cpu', cpu, 85, 'The server\'s tasks use more than 85% of their CPU.');
    alarm('database-capacity', databaseCapacity, config.database.maxAcu * 0.9, `The database is near its maximum of ${config.database.maxAcu} ACUs.`, 3,
      cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD);
    alarm('database-cpu', databaseCpu, 80, 'The database uses more than 80% of its CPU.');
    alarm('table-throttling', throttled, 0, 'Requests to the histories\' table were throttled.', 1);
    alarm('table-errors', systemErrors, 0, 'The histories\' table answered with system errors.', 1);
    alarm('storage-growth', bucketSize, config.storageAlarmGiB * 1024 ** 3, `The histories' bucket holds more than ${config.storageAlarmGiB} GiB.`, 1);

    new cloudwatch.Dashboard(this, 'Dashboard', {
      dashboardName: config.envName,
      widgets: [
        [
          new cloudwatch.GraphWidget({ title: 'Requests', left: [requests], width: 8 }),
          new cloudwatch.GraphWidget({ title: 'Errors (5xx)', left: [errors], width: 8 }),
          new cloudwatch.GraphWidget({ title: 'Response time p99 (s)', left: [latency], width: 8 }),
        ],
        [
          new cloudwatch.GraphWidget({ title: 'Server tasks', left: [running], right: [unhealthy], width: 8 }),
          new cloudwatch.GraphWidget({ title: 'Server CPU and memory (%)', left: [cpu, memory], width: 8 }),
          new cloudwatch.GraphWidget({ title: 'Database ACUs, CPU, connections', left: [databaseCapacity, connections], right: [databaseCpu], width: 8 }),
        ],
        [
          new cloudwatch.GraphWidget({ title: 'Table throttling and errors', left: [throttled, systemErrors], width: 12 }),
          new cloudwatch.GraphWidget({ title: 'Histories bucket (bytes)', left: [bucketSize], width: 12 }),
        ],
        [new cloudwatch.AlarmStatusWidget({ title: 'Alarms', alarms: this.alarms, width: 24 })],
      ],
    });

    new budgets.CfnBudget(this, 'Budget', {
      budget: {
        budgetName: config.envName,
        budgetType: 'COST',
        timeUnit: 'MONTHLY',
        budgetLimit: { amount: config.monthlyBudgetUsd, unit: 'USD' },
      },
      notificationsWithSubscribers: [
        { notification: { notificationType: 'ACTUAL', comparisonOperator: 'GREATER_THAN', threshold: 80, thresholdType: 'PERCENTAGE' },
          subscribers: [{ subscriptionType: 'EMAIL', address: config.alarmEmail }] },
        { notification: { notificationType: 'FORECASTED', comparisonOperator: 'GREATER_THAN', threshold: 100, thresholdType: 'PERCENTAGE' },
          subscribers: [{ subscriptionType: 'EMAIL', address: config.alarmEmail }] },
      ],
    });
  }
}
