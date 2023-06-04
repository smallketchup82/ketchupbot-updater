# Ketchupbot101
Automatic Galaxypedia Updater Bot

## Schedules
Automatically updates ships at XX:00  
Automatically updates turrets page at XX:30

## Installation
Ketchupbot is primarily meant to be installed and used with docker, but it doesn't have to be. The application is stateless and doesnt persist data.

### First steps
1. Clone the repository
2. Rename `.env.example` to `.env`
3. Fill it out

### With Docker
Recommended method

1. Cd into the directory root
2. Run `docker compose up -d` to start it

If you make any changes, run `docker compose up -d --build` to rebuild the program and recreate/restart the program  

### Without Docker
Make sure you have Node.js version 18

1. Run `npm install` to install all dependencies. Optionally do `npm install --omit=dev` to skip installing development dependencies if you only intend on running it, not changing the source code
2. Run `npm install typescript` to install the typescript compiler
3. Run `npx tsc` to compile the program
4. Run `node .` while in the document root to start the bot

## Using as a dependency
Ketchupbot can be used in other programs as a dependency with a bit more work

### Setting it up
1. Copy `src/index.ts` into your projects `src` folder and rename it to `ketchuplib.ts`
2. Copy the dependencies from `package.json` to your projects `package.json`
3. Copy `banner.txt` to your project root
4. Copy the parameters from `.env.example` to your project's `.env` and fill it out

### Initializing
Import ketchupbot into your project
```ts
import * as ketchuplib from "./ketchuplib"
```
Initalize the library
```ts
// Initialize the ShipUpdater class. Also export it so that other files can call its functions
export const ketchupbot = new ketchuplib.ShipUpdater()

// Initialize the nodemw instance. Logs into your mediawiki account
const mwbot = await ketchuplib.initBot()

// Initialize the loggers which provide insight on the application's status
const { logChange, logDiscord } = await ketchuplib.initLoggers()

// Start the updater system
// First argument passes the nodemw bot instance to the function
// 2nd and 3rd arguments pass the webhook loggers
// 4th argument sets the singlepass boolean to true, which shuts off the scheduling and lets you control the scheduling manually
const updater = await ketchupbot.main(mwbot, logChange, logDiscord, true)
```
From here on you can use the `ketchupbot` variable to run functions like `updateGalaxypediaShips()` manually
Refer to `ketchuplib.ts` to see what functions you can run

## Inner workings
WIP