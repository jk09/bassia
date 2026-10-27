#!/usr/bin/env node
import * as cdk from 'aws-cdk-lib';
import { addEnvironment } from '../lib/app';
import { checkConfig, loadConfig } from '../lib/config';

// The environment to synthesize or deploy: cdk deploy -c env=staging --all (default: production). A release deploys the
// images it published with -c imageTag=<tag>.
const app = new cdk.App();
const config = loadConfig(app.node.tryGetContext('env') ?? 'production');
const imageTag = app.node.tryGetContext('imageTag');
if (imageTag) {
  config.server.imageTag = imageTag;
  config.workers.imageTag = imageTag;
}

addEnvironment(app, checkConfig(config));
