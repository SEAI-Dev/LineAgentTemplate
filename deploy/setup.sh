#!/bin/bash
# LINE Agent - VM Deployment Script
# Usage: bash deploy/setup.sh
set -e

APP_DIR="/opt/lineagent"
SERVICE_NAME="lineagent"

echo "=== LINE Agent VM Setup ==="

# Install .NET runtime
if ! command -v dotnet &> /dev/null; then
    echo "Installing .NET runtime..."
    sudo mkdir -p /opt/dotnet
    wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    sudo /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir /opt/dotnet
    sudo ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
    echo "dotnet $(dotnet --version) installed"
fi

# Deploy app
sudo mkdir -p $APP_DIR
if [ -f /tmp/lineagent-publish.tar.gz ]; then
    sudo rm -rf $APP_DIR/*
    sudo tar -xzf /tmp/lineagent-publish.tar.gz -C $APP_DIR
    sudo chown -R $(whoami):$(whoami) $APP_DIR
    echo "App deployed to $APP_DIR"
fi

# Create systemd service
sudo tee /etc/systemd/system/${SERVICE_NAME}.service > /dev/null << EOF
[Unit]
Description=LINE Agent API
After=network.target

[Service]
Type=simple
User=$(whoami)
WorkingDirectory=$APP_DIR
ExecStart=/opt/dotnet/dotnet $APP_DIR/LineAgent.Api.dll --urls http://0.0.0.0:5010
Environment=DOTNET_ROOT=/opt/dotnet
Environment=LINEAGENT_DB=sqlite
Environment=LINE_CHANNEL_SECRET=${LINE_CHANNEL_SECRET}
Environment=LINE_CHANNEL_ACCESS_TOKEN=${LINE_CHANNEL_ACCESS_TOKEN}
Environment=LINE_DEFAULT_USER_ID=${LINE_DEFAULT_USER_ID}
Environment=TZ=Asia/Taipei
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable $SERVICE_NAME
sudo systemctl restart $SERVICE_NAME
sleep 3
echo "Service status: $(sudo systemctl is-active $SERVICE_NAME)"

# Install ngrok
if ! command -v ngrok &> /dev/null; then
    echo "Installing ngrok..."
    wget -q https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-linux-amd64.tgz -O /tmp/ngrok.tgz
    sudo tar -xzf /tmp/ngrok.tgz -C /usr/local/bin
fi
echo "ngrok $(ngrok version)"

# Setup ngrok service
if [ -n "$NGROK_AUTHTOKEN" ]; then
    ngrok config add-authtoken $NGROK_AUTHTOKEN

    sudo tee /etc/systemd/system/ngrok-lineagent.service > /dev/null << EOF
[Unit]
Description=ngrok tunnel for LINE Agent
After=network.target ${SERVICE_NAME}.service

[Service]
Type=simple
User=$(whoami)
ExecStart=/usr/local/bin/ngrok http 5010 --log=stdout
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

    sudo systemctl daemon-reload
    sudo systemctl enable ngrok-lineagent
    sudo systemctl restart ngrok-lineagent
    sleep 3

    NGROK_URL=$(curl -s http://localhost:4040/api/tunnels | python3 -c 'import sys,json; t=json.load(sys.stdin)["tunnels"]; print(t[0]["public_url"] if t else "")' 2>/dev/null)
    echo ""
    echo "=== Setup Complete ==="
    echo "API: http://localhost:5010"
    echo "Public URL: $NGROK_URL"
    echo "Webhook URL: ${NGROK_URL}/api/line/webhook"
    echo ""
    echo "Set this Webhook URL in LINE Developers Console:"
    echo "https://developers.line.biz/console/"
else
    echo ""
    echo "=== Setup Complete (no ngrok) ==="
    echo "API: http://localhost:5010"
    echo "To enable ngrok, run: NGROK_AUTHTOKEN=xxx bash deploy/setup.sh"
fi
