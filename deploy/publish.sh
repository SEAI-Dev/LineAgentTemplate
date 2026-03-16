#!/bin/bash
# Build and upload to VM via gcloud
# Usage: bash deploy/publish.sh <project-id> <vm-name> <zone>
set -e

PROJECT=${1:-"twstocker"}
VM=${2:-"twstocker-vm"}
ZONE=${3:-"us-west1-b"}

echo "=== Publishing LineAgent.Api ==="
cd src/LineAgent.Api
dotnet publish -c Release -r linux-x64 --self-contained false -o ../../publish
cd ../..

echo "=== Packaging ==="
tar -czf lineagent-publish.tar.gz -C publish .

echo "=== Uploading to $VM ==="
gcloud compute scp lineagent-publish.tar.gz ${VM}:/tmp/lineagent-publish.tar.gz --project=$PROJECT --zone=$ZONE

echo "=== Deploying ==="
gcloud compute ssh $VM --project=$PROJECT --zone=$ZONE --command="bash /tmp/setup.sh 2>/dev/null || (sudo systemctl stop lineagent 2>/dev/null; sudo rm -rf /opt/lineagent/*; sudo tar -xzf /tmp/lineagent-publish.tar.gz -C /opt/lineagent; sudo chown -R \$(whoami):\$(whoami) /opt/lineagent; sudo systemctl start lineagent; sleep 2; echo Status: \$(sudo systemctl is-active lineagent))"

rm -f lineagent-publish.tar.gz
echo "=== Done ==="
