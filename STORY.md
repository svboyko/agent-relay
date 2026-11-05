I'm used to work in Windows (yes, i understand how many rotten tomatoes threw to me... :) ). I'm used to use tools like Far Manager, Winmerge, Tortoise... But working with PHP demand to work in linux environment. WSL was a good answer.

But WSL has a bottle neck - drvfs. You have 2 options to use WSL in development
- keep files in windows and mount windows dir into WSL via drvfs. In this case you have slow file system in WSL because of drvfs.
- keep files inside WSL. It's ok for WSL, but now you have troubles with working with that files from Windows. Off course you can open then via \\wsl network connection, but it's even more slow then drvfs

Editors like VSCode, Zed propose their plugins to work with WSL. They are nice, but:
- if you keep files in WSL, all works nice, but you actually moves to linux. You almost cannot use windows tools for that files.
- if you keep files in Windwos and mount to WSL, your CPU fun start spinning because editor start to scan your project folder every several seconds because it does not have other way to catch changes.

So, i do not use thoses plugins, opens editor in windows dir, just open their terminal in WSL. After some configuration it works nice. You use windows tools in windows, linux systems (apache etc) get them from mounted dir. while you keep only sources in mounted dir, but caches/temp/DB etc inside WSL, it's ok

Then Agents appeared. At begin i started to call them in Windows, but
- it's good to have them closer to run environment, so they can do testing etc.
- security concerns - some my security experiments showed me that i cannot trust them, so it was obvious that it's better to run them in isolated environment.

So, WSL looks as an answer for another problem - isolated environment for agents (off course after some configuration,, like disable auto-mount, run windows apps etc...)

But i've already got used to zed agent panel. So, 2 options again:
- start using zed WSL plugin (Remote-WSL). And hear the fun noice.
- find a way to bridge windows zed agent panel with WSL agents

My nature pushed me second way. And this project appeared.
