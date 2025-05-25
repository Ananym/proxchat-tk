# Required Tools for Multi-Architecture Builds

## Already Available

✅ **Docker Desktop** - You have this with buildx support  
✅ **PowerShell** - For running build scripts  
✅ **Rust toolchain** - For local development

## For AWS Deployment

You'll need these when ready to deploy:

### AWS CLI

```powershell
# Install via Chocolatey (if you have it)
choco install awscli

# Or download from: https://aws.amazon.com/cli/
```

### AWS Account Setup

- AWS account with ECR and ECS permissions
- Configured AWS credentials (`aws configure`)

## What's Already Set Up

### ✅ Multi-Architecture Docker Support

- `.\build.ps1 -Docker -MultiArch` builds for both x86 and ARM
- Automatically creates buildx builder instance
- No additional setup needed

### ✅ Local Development

- `.\build.ps1` for local Rust builds
- `.\build.ps1 -Docker -Run` for local testing
- Works on your existing Windows/x86 setup

### ✅ ARM Emulation

- Docker Desktop includes QEMU for ARM emulation
- Can test ARM builds locally (though slower)

## Cost Benefits

- **ARM instances are ~50% cheaper** than x86 on AWS
- **Lightweight server** perfect for t4g.nano or Fargate ARM
- **Multi-arch image** means same container works everywhere

## Next Steps When Ready

1. Install AWS CLI
2. Create ECR repository
3. Follow `AWS-deployment-guide.md`
4. Deploy with: `.\build.ps1 -Docker -MultiArch -Push -Registry <your-ecr>`

**No additional tools needed for development right now!**
