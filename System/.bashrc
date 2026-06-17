# ~/.bashrc: executed by bash(1) for non-login shells.
# see /usr/share/doc/bash/examples/startup-files (in the package bash-doc)
# for examples

# If not running interactively, don't do anything
case $- in
    *i*) ;;
      *) return;;
esac

# don't put duplicate lines or lines starting with space in the history.
# See bash(1) for more options
HISTCONTROL=ignoreboth

# append to the history file, don't overwrite it
shopt -s histappend

# for setting history length see HISTSIZE and HISTFILESIZE in bash(1)
HISTSIZE=1000
HISTFILESIZE=2000

# check the window size after each command and, if necessary,
# update the values of LINES and COLUMNS.
shopt -s checkwinsize

# If set, the pattern "**" used in a pathname expansion context will
# match all files and zero or more directories and subdirectories.
#shopt -s globstar

# make less more friendly for non-text input files, see lesspipe(1)
[ -x /usr/bin/lesspipe ] && eval "$(SHELL=/bin/sh lesspipe)"

# set variable identifying the chroot you work in (used in the prompt below)
if [ -z "${debian_chroot:-}" ] && [ -r /etc/debian_chroot ]; then
    debian_chroot=$(cat /etc/debian_chroot)
fi

# set a fancy prompt (non-color, unless we know we "want" color)
case "$TERM" in
    xterm-color|*-256color) color_prompt=yes;;
esac

# uncomment for a colored prompt, if the terminal has the capability; turned
# off by default to not distract the user: the focus in a terminal window
# should be on the output of commands, not on the prompt
#force_color_prompt=yes

if [ -n "$force_color_prompt" ]; then
    if [ -x /usr/bin/tput ] && tput setaf 1 >&/dev/null; then
	# We have color support; assume it's compliant with Ecma-48
	# (ISO/IEC-6429). (Lack of such support is extremely rare, and such
	# a case would tend to support setf rather than setaf.)
	color_prompt=yes
    else
	color_prompt=
    fi
fi

if [ "$color_prompt" = yes ]; then
    PS1='${debian_chroot:+($debian_chroot)}\[\033[01;32m\]\u@\h\[\033[00m\]:\[\033[01;34m\]\w\[\033[00m\]\$ '
else
    PS1='${debian_chroot:+($debian_chroot)}\u@\h:\w\$ '
fi
unset color_prompt force_color_prompt

# If this is an xterm set the title to user@host:dir
case "$TERM" in
xterm*|rxvt*)
    PS1="\[\e]0;${debian_chroot:+($debian_chroot)}\u@\h: \w\a\]$PS1"
    ;;
*)
    ;;
esac

# enable color support of ls and also add handy aliases
if [ -x /usr/bin/dircolors ]; then
    test -r ~/.dircolors && eval "$(dircolors -b ~/.dircolors)" || eval "$(dircolors -b)"
    alias ls='ls --color=auto'
    #alias dir='dir --color=auto'
    #alias vdir='vdir --color=auto'

    alias grep='grep --color=auto'
    alias fgrep='fgrep --color=auto'
    alias egrep='egrep --color=auto'
fi

# colored GCC warnings and errors
#export GCC_COLORS='error=01;31:warning=01;35:note=01;36:caret=01;32:locus=01:quote=01'

# some more ls aliases
alias ll='ls -alF'
alias la='ls -A'
alias l='ls -CF'

# Add an "alert" alias for long running commands.  Use like so:
#   sleep 10; alert
alias alert='notify-send --urgency=low -i "$([ $? = 0 ] && echo terminal || echo error)" "$(history|tail -n1|sed -e '\''s/^\s*[0-9]\+\s*//;s/[;&|]\s*alert$//'\'')"'

# Alias definitions.
# You may want to put all your additions into a separate file like
# ~/.bash_aliases, instead of adding them here directly.
# See /usr/share/doc/bash-doc/examples in the bash-doc package.

if [ -f ~/.bash_aliases ]; then
    . ~/.bash_aliases
fi

# enable programmable completion features (you don't need to enable
# this, if it's already enabled in /etc/bash.bashrc and /etc/profile
# sources /etc/bash.bashrc).
if ! shopt -oq posix; then
  if [ -f /usr/share/bash-completion/bash_completion ]; then
    . /usr/share/bash-completion/bash_completion
  elif [ -f /etc/bash_completion ]; then
    . /etc/bash_completion
  fi
fi

# =====================================================
# CHATOPS SYSTEM MANAGEMENT ALIASES & FUNCTIONS (FIXED)
# =====================================================

# Hàm quản lý restart dịch vụ
restart() {
    if [ "$1" = "backend" ]; then
        echo "🔄 Đang khởi động lại ChatOps Backend..."
        sudo systemctl restart chatops-backend && echo "✅ Backend đã được khởi động lại!"
    elif [ "$1" = "frontend" ]; then
        echo "🔄 Kiểm tra cấu hình và nạp lại OpenResty (Frontend)..."
        # Kiểm tra cú pháp cấu hình trước, nếu OK mới thực hiện reload dịch vụ an toàn
        if sudo openresty -t; then
            sudo systemctl reload openresty && echo "✅ OpenResty đã nạp lại cấu hình thành công!"
        else
            echo "❌ Cấu hình OpenResty bị lỗi! Không thể reload."
        fi
    elif [ "$1" = "redis" ]; then
        echo "🔄 Đang khởi động lại Redis Server..."
        sudo systemctl restart redis-server && echo "✅ Redis Server đã khởi động lại!"
    elif [ "$1" = "commander" ]; then
        echo "🔄 Đang khởi động lại Redis Commander Web..."
        sudo systemctl restart redis-commander && echo "✅ Redis Commander đã khởi động lại!"
    else
        echo "❌ Dùng: restart backend | frontend | redis | commander"
    fi
}

# Hàm quản lý bật dịch vụ
start() {
    if [ "$1" = "backend" ]; then
        sudo systemctl start chatops-backend && echo "▶️ Đã bật Backend."
    elif [ "$1" = "frontend" ]; then
        echo "▶️ Đang khởi động dịch vụ OpenResty..."
        sudo systemctl start openresty && echo "✅ Đã bật OpenResty."
    elif [ "$1" = "redis" ]; then
        sudo systemctl start redis-server && echo "▶️ Đã bật Redis Server."
    elif [ "$1" = "commander" ]; then
        sudo systemctl start redis-commander && echo "▶️ Đã bật Redis Commander Web."
    else
        echo "❌ Dùng: start backend | frontend | redis | commander"
    fi
}

# Hàm quản lý tắt dịch vụ
stop() {
    if [ "$1" = "backend" ]; then
        sudo systemctl stop chatops-backend && echo "⏹️ Đã tắt Backend."
    elif [ "$1" = "frontend" ]; then
        echo "⚠️ Cảnh báo: Tắt OpenResty sẽ sập toàn bộ hệ thống Gateway!"
        sudo systemctl stop openresty && echo "⏹️ Đã tắt OpenResty."
    elif [ "$1" = "redis" ]; then
        echo "⚠️ Cảnh báo: Tắt Redis có thể làm gián đoạn định tuyến ChatOps!"
        sudo systemctl stop redis-server && echo "⏹️ Đã tắt Redis Server."
    elif [ "$1" = "commander" ]; then
        sudo systemctl stop redis-commander && echo "⏹️ Đã tắt Redis Commander Web."
    else
        echo "❌ Dùng: stop backend | frontend | redis | commander"
    fi
}

# Hàm xem logs dịch vụ
logs() {
    if [ "$1" = "backend" ]; then
        journalctl -u chatops-backend -n 50 -f
    elif [ "$1" = "frontend" ]; then
        echo "📋 Đang xem log truy cập của OpenResty..."
        if [ -f /usr/local/openresty/nginx/logs/access.log ]; then
            sudo tail -f /usr/local/openresty/nginx/logs/access.log
        else
            sudo tail -f /var/log/openresty/access.log
        fi
    elif [ "$1" = "error" ]; then
        echo "❌ Đang xem log lỗi của OpenResty..."
        if [ -f /usr/local/openresty/nginx/logs/error.log ]; then
            sudo tail -f /usr/local/openresty/nginx/logs/error.log
        else
            sudo tail -f /var/log/openresty/error.log
        fi
    elif [ "$1" = "redis" ]; then
        echo "📋 Đang xem log của Redis Server..."
        sudo journalctl -u redis-server -n 50 -f
    elif [ "$1" = "commander" ]; then
        echo "📋 Đang xem log của Redis Commander Web..."
        sudo journalctl -u redis-commander -n 50 -f
    else
        echo "❌ Dùng: logs backend | frontend | error | redis | commander"
    fi
}
