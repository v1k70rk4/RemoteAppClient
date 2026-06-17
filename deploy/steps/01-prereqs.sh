# Step 01 - base packages needed by the rest of the steps.
require_sudo
sudo DEBIAN_FRONTEND=noninteractive apt-get update -qq
sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
  curl ca-certificates openssl tar coreutils jq
ok "base packages installed"
