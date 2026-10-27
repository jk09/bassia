import * as cdk from 'aws-cdk-lib';
import * as ec2 from 'aws-cdk-lib/aws-ec2';
import { Construct } from 'constructs';
import { EnvironmentConfig } from './config';

export interface NetworkStackProps extends cdk.StackProps {
  config: EnvironmentConfig;
}

/**
 * The VPC across three availability zones: public subnets for the load balancer and the NAT gateways, private
 * subnets for the server, its tasks and agent workers, isolated subnets for the database. S3 and DynamoDB are reached
 * through gateway endpoints, so history traffic never crosses a NAT gateway.
 */
export class NetworkStack extends cdk.Stack {
  public readonly vpc: ec2.Vpc;

  constructor(scope: Construct, id: string, props: NetworkStackProps) {
    super(scope, id, props);
    const { config } = props;

    this.vpc = new ec2.Vpc(this, 'Vpc', {
      vpcName: config.envName,
      availabilityZones: config.availabilityZones,
      natGateways: config.natGateways,
      ipAddresses: ec2.IpAddresses.cidr('10.40.0.0/16'),
      subnetConfiguration: [
        { name: 'public', subnetType: ec2.SubnetType.PUBLIC, cidrMask: 24 },
        { name: 'compute', subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS, cidrMask: 20 },
        { name: 'data', subnetType: ec2.SubnetType.PRIVATE_ISOLATED, cidrMask: 24 },
      ],
      gatewayEndpoints: {
        S3: { service: ec2.GatewayVpcEndpointAwsService.S3 },
        DynamoDB: { service: ec2.GatewayVpcEndpointAwsService.DYNAMODB },
      },
    });

    this.vpc.addFlowLog('FlowLog', { trafficType: ec2.FlowLogTrafficType.REJECT });
  }
}
