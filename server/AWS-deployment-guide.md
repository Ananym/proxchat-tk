# AWS ECS Deployment Guide

This guide shows how to deploy the prox-chat-server to AWS ECS using ARM instances.

## Prerequisites

- AWS CLI installed and configured
- Docker with buildx support
- An AWS account with appropriate permissions

## Step 1: Create ECR Repository

```bash
# Create ECR repository
aws ecr create-repository --repository-name prox-chat-server --region us-east-1

# Note the repository URI from the output, e.g.:
# 123456789012.dkr.ecr.us-east-1.amazonaws.com/prox-chat-server
```

## Step 2: Build and Push Multi-Architecture Image

```powershell
# Login to ECR
aws ecr get-login-password --region us-east-1 | docker login --username AWS --password-stdin 123456789012.dkr.ecr.us-east-1.amazonaws.com

# Build and push multi-arch image (supports both x86 and ARM)
.\build.ps1 -Docker -MultiArch -Push -Registry "123456789012.dkr.ecr.us-east-1.amazonaws.com/prox-chat-server" -Tag "latest"
```

## Step 3: Create ECS Task Definition

Create a file `task-definition.json`:

```json
{
  "family": "prox-chat-server",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "256",
  "memory": "512",
  "runtimePlatform": {
    "cpuArchitecture": "ARM64",
    "operatingSystemFamily": "LINUX"
  },
  "executionRoleArn": "arn:aws:iam::123456789012:role/ecsTaskExecutionRole",
  "containerDefinitions": [
    {
      "name": "prox-chat-server",
      "image": "123456789012.dkr.ecr.us-east-1.amazonaws.com/prox-chat-server:latest",
      "portMappings": [
        {
          "containerPort": 8080,
          "protocol": "tcp"
        }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "/ecs/prox-chat-server",
          "awslogs-region": "us-east-1",
          "awslogs-stream-prefix": "ecs"
        }
      }
    }
  ]
}
```

## Step 4: Create CloudWatch Log Group

```bash
aws logs create-log-group --log-group-name /ecs/prox-chat-server --region us-east-1
```

## Step 5: Register Task Definition

```bash
aws ecs register-task-definition --cli-input-json file://task-definition.json --region us-east-1
```

## Step 6: Create ECS Cluster

```bash
aws ecs create-cluster --cluster-name prox-chat-cluster --capacity-providers FARGATE --region us-east-1
```

## Step 7: Create ECS Service

```bash
aws ecs create-service \
  --cluster prox-chat-cluster \
  --service-name prox-chat-service \
  --task-definition prox-chat-server \
  --desired-count 1 \
  --launch-type FARGATE \
  --network-configuration "awsvpcConfiguration={subnets=[subnet-12345678],securityGroups=[sg-12345678],assignPublicIp=ENABLED}" \
  --region us-east-1
```

## Cost Optimization Tips

- Use ARM-based Fargate instances (50% cheaper than x86)
- Start with minimal CPU/memory allocation
- Use spot capacity if appropriate for your use case
- Consider using Application Load Balancer only if you need multiple instances

## Local Testing with ARM Emulation

You can test the ARM build locally:

```powershell
# Build ARM image locally for testing
.\build.ps1 -Docker -Platform "linux/arm64"

# Run with emulation (will be slower)
docker run -p 8080:8080 prox-chat-server:latest
```
