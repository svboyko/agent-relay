# How Agent Relay Started

## Windows, WSL, and a Spinning Fan

I'm used to working on Windows (yes, I can already see the rotten tomatoes coming... :) ). I'm used to tools like Far Manager, WinMerge, and Tortoise. But for my PHP work, I need a Linux environment. WSL was a good answer.

But WSL has a bottleneck: DrvFs. When using WSL for development, you have two options:

- Keep files on Windows and mount the directory in WSL through DrvFs. File access from WSL is slow.
- Keep files inside WSL. That works well for Linux, but working with those files from Windows becomes awkward. Of course, you can access them through the `\\wsl$` network path, but in my experience, that's even slower than DrvFs.

Editors like VS Code and Zed offer WSL integrations. They are nice, but:

- If you keep files in WSL, everything works well, but you've effectively moved to Linux. Using your usual Windows tools on those files becomes difficult.
- If you keep files on Windows and mount them in WSL, your CPU fan starts spinning. In my setup, the editor kept scanning the project folder every few seconds to detect changes.

So I don't use those integrations. I open the editor in a Windows directory and use WSL in its terminal. After some configuration, this works well. I use Windows tools on Windows, and Linux services like Apache access the files through the mounted directory. As long as I keep only source files there, with caches, temporary files, and databases inside WSL, it's fine.

## Then Came the Agents

At first, I ran coding agents on Windows. But I had two reasons to move them:

- It's useful to have them close to the runtime environment, so they can run tests and do other checks.
- Some security experiments convinced me that I couldn't trust them with unrestricted access. I wanted to run them in an isolated environment.

So WSL looked like an answer to another problem: a separate environment for agents. Of course, that needed some configuration too, such as disabling automatic drive mounting and the ability to run Windows applications.

But I'd already got used to Zed's agent panel. So, two options again:

- Start using Zed's WSL integration and listen to the fan noise.
- Find a way to connect Zed's agent panel on Windows to agents running in WSL.

My nature pushed me toward the second option. And that's how Agent Relay started.
