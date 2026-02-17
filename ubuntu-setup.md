## Setting up WSL Ubuntu

# 0. In a Windows Terminal...

# 1. Uninstall (wipe) Ubuntu
wsl --shutdown
wsl --unregister Ubuntu

# 2. Install Ubuntu
wsl --install -d Ubuntu
# (Ubuntu will launch — create your username/password when prompted)

# 3. Disable Windows PATH in WSL (optional)
sudo bash -c 'cat >> /etc/wsl.conf << EOF
[interop]
appendWindowsPath = false
EOF'

# Restart WSL (from Windows PowerShell)
# close Ubuntu session
exit
# Start Ubuntu session
wsl -d Ubuntu

# 4. Adjust prompts (optional)
# --- NORMAL USER PROMPT (green username$) ---
sed -i '/^export PS1=/d' ~/.bashrc
echo "export PS1='\[\e[32m\]\u\[\e[0m\]\$ '" >> ~/.bashrc
source ~/.bashrc
# --- ROOT PROMPT (red root#) ---
sudo sed -i '/^export PS1=/d' /root/.bashrc
sudo bash -c "echo \"export PS1='\\[\\e[31m\\]\\u\\[\\e[0m\\]# '\" >> /root/.bashrc"
# --- Always start in Linux home ---
echo "cd ~" >> ~/.bashrc
source ~/.bashrc

# 5. Install Node.js (restart Ubuntu first)
curl -fsSL https://deb.nodesource.com/setup_lts.x | sudo -E bash -
sudo apt install -y nodejs

# 6. Upgrade existing packages
sudo apt update && sudo apt upgrade -y && sudo apt autoremove -y

# 7. Install git
sudo apt install -y git

# 8. Install SVN
sudo apt install -y subversion

# 9. Pull down an SVN repo
mkdir -p ~/ir_local
cd ~/ir_local
svn checkout http://10.130.61.10/work2/svn/repos/SBN_IR/trunk .

# 9a. Setup shortcut (optional)
vim ~/.bashrc
export ir="$HOME/ir_local"
. ~/.bashrc

# FINAL. Install compilers (see readme.md for full details)
curl -fsSL https://raw.githubusercontent.com/innovative247/compilers/main/install.sh | bash
# Then: set_profile configure
