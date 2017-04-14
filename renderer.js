// This file is required by the index.html file and will
// be executed in the renderer process for that window.
// All of the Node.js APIs are available in this process.
"use strict";

const fs = require('fs');
const path = require('path');
const assert = require('assert');
const request = require('request');
const mkdirp = require('mkdirp');
const os = require('os');
const child_process = require('child_process');
const url = require("url");

const sha3_256 = require('js-sha3').sha3_256;
const $ = require('jquery');

var {ipcRenderer, remote} = require('electron');

const versionInfo = require('./version_info');
const retrieveNews = require('./retrieve_news');
const { Modal } = require('./modal');
const { unpackRelease, findBinInRelease } = require('./unpack');

//
// Settings thing
//
const fetchNewsFromWeb = true;

const linksModal = new Modal("linksModal", "linksModalDialog", {
    closeButton: "linksClose"
});

var playModalQuitDLCancel = null;
var currentDLCanceled = false;

const playModal = new Modal("playModal", "playModalDialog", {
    autoClose: false,
    closeButton: "playModalClose",
    onClose: function(){

        // Cancel download //
        if(playModalQuitDLCancel){

            currentDLCanceled = true;
            playModalQuitDLCancel.abort();
            playModalQuitDLCancel = null;
        }

        // Prevent closing //
        //return true;
    }
});

// http://ourcodeworld.com/articles/read/228/how-to-download-a-webfile-with-electron-save-it-and-show-download-progress
// With some modifications
function downloadFile(configuration){
    return new Promise(function(resolve, reject){
        // Save variable to know progress
        var received_bytes = 0;
        var total_bytes = 0;

        var req = request({
            method: 'GET',
            uri: configuration.remoteFile
        });

        configuration.reqObj = req;

        var out = fs.createWriteStream(configuration.localFile, { encoding: null });
        //req.pipe(out);

        let contentType = "unknown";

        req.on('response', function ( data ) {
            // Change the total bytes value to get progress later.
            total_bytes = parseInt(data.headers['content-length' ]);

            contentType = data.headers['content-type'];
        });

        // Get progress if callback exists
        if(configuration.hasOwnProperty("onProgress")){
            req.on('data', function(chunk) {
                // Update the received bytes
                received_bytes += chunk.length;

                configuration.onProgress(received_bytes, total_bytes);

                out.write(chunk)
            });
        }else{
            req.on('data', function(chunk) {
                // Update the received bytes
                received_bytes += chunk.length;
                out.write(chunk)
            });
        }

        req.on('end', function() {
            out.end();
            resolve(contentType);
        });

        req.on('error', function(err){

            out.end();
            fs.unlinkSync(configuration.localFile);
            reject(err);
            
        });
    });
}





//! Parses version information from data and adds it to all the places
function onVersionDataReceived(data){

    versionInfo.parseData(data);

    assert(versionInfo.getVersionData().versions);
    
    updatePlayButton();
}

// Load dummy version data //
fs.readFile(path.join(remote.app.getAppPath(), 'test/data/thrive_versions.json'),
            "utf8",
            function (err,data){
                
                if (err) {
                    return console.log(err);
                }

                onVersionDataReceived(data);
            });


// Buttons
let playButton = document.getElementById("playButton");

let playButtonText = document.getElementById("playText");

playButtonText.textContent = "Retrieving version information...";



const dlPath = "staging/download/";
const installPath = "installed/";

//! Verifies file hash
//! returns a promise that either cusseeds or fails once the check is done
function verifyDLHash(version, download, localTarget){

    return new Promise((resolve, reject) => {

        const totalSize = fs.statSync(localTarget).size;

        let hasher = sha3_256.create();

        let readable = fs.createReadStream(localTarget, { encoding: null });

        let status = document.getElementById("playHashProgress");
        
        readable.on('data', (chunk) => {

            let percentage = (readable.bytesRead * 100) / totalSize;

            if(status)
                status.textContent = "Progress " + percentage.toFixed(2) + "%";
            
            hasher.update(chunk);
        });

        readable.on('end', () => {

            const fileHash = hasher.hex();
            
            if(download.hash != fileHash){
                
                console.error("Hashes don't match! " + download.hash + " != " + fileHash);

                reject();
                return;
            }
            
            resolve();
        });
        
    });
}

function onThriveFolderReady(version, download){

    const installFolder = path.join(installPath, download.folderName);
    
    assert(fs.existsSync(installFolder));

    let status = document.getElementById("playingInternalP");

    // Destroy the download progress indicator
    status.innerHTML = "";

    status.textContent = "preparing to launch";

    // Find bin folder //
    let binFolder = findBinInRelease(installFolder);

    if(!fs.existsSync(binFolder)){

        status.textContent = "Error 'bin' folder is missing! " + (binFolder ? binFolder : "");
        return;
    }

    // Check that executable is there //
    let exename;
    
    if(os.platform() == "win32"){

        exename = "Thrive.exe";
        
    } else {

        exename = "Thrive";
    }

    if(!fs.existsSync(path.join(binFolder, exename))){

        status.textContent = "Error: Thrive executable is missing!";
        return;
    }

    status.textContent = "launching...";

    let thrive = child_process.spawn(path.join(remote.app.getAppPath(), binFolder, exename),
                                     [],
                                     {
                                         cwd: binFolder
                                     });

    status.innerHTML = "";

    let titleSpan = document.createElement("span");

    let processOutput = document.createElement("div");

    processOutput.style.overflow = "auto";
    // This needs a fixed size for some reason
    processOutput.style.maxHeight = "420px";
    processOutput.style.height = "420px";
    processOutput.style.paddingTop = "5px";
    processOutput.style.width = "100%";

    status.style.marginBottom = "1px";

    titleSpan.textContent = "Thrive is running. Log output: ";

    titleSpan.append(processOutput);

    status.append(titleSpan);

    let appendMessage = (text) => {

        let message = document.createElement("div");
        message.textContent = text;
        processOutput.append(message);

        let container = $( processOutput );

        // Max number of messages
        while(container.children().length > 1000){

            // Remove elements //
            container.children().first().remove();
        }

        // For some reason the jquery thing is not working so this is at least a decent choice
        message.scrollIntoView(false);

        // let modalContainer = $( playModal.dialog );

        // let check = $("#playModalDialog");

        // //assert(modalContainer == check);

        // check.scrollTop = check.scrollHeight;

        // // modalContainer.animate({
        // //     scrollTop: modalContainer.scrollHeight
        // // }, 500);
    };

    appendMessage("Process Started");
    
    thrive.stdout.on('data', (data) => {

        appendMessage(data);
    });

    thrive.stderr.on('data', (data) => {

        appendMessage("ERROR: " + data);
    });

    thrive.on('close', (code) => {
        
        console.log(`child process exited with code ${code}`);
        appendMessage(`child process exited with code ${code}`);

        if(code == 0)
            appendMessage("Thrive has exited normally.");

        let closeContainer = document.createElement("div");

        closeContainer.style.textAlign = "center";
        
        let close = document.createElement("div");

        close.textContent = "Close";

        close.className = "AfterPlayClose";

        close.addEventListener('click', (event) => {

            console.log("extra close clicked");
            playModal.hide();
        });

        closeContainer.append(close);
        
        status.append(closeContainer);

    });
}

//! Called once a file has been downloaded (or existed) and startup should continue
function onDLFileReady(version, download, fileName){

    // Delete the download progress //
    $( "#dlProgress" ).remove();
        

    const localTarget = dlPath + fileName;
    
    assert(fs.existsSync(localTarget));

    let status = document.getElementById("playingInternalP");

    // Destroy the download progress indicator
    status.innerHTML = "";

    // If unpacked already launch Thrive //
    mkdirp(installPath, function (err){
        
        if(err){
            
            console.error(err);
            alert("failed to create install directory");
            return;
        }

        // Check does it exist //
        if(fs.existsSync(path.join(installPath, download.folderName))){

            console.log("archive has already been extracted");
            onThriveFolderReady(version, download);
            return;
        }

        // Need to unpack //


        // Hash is verified before unpacking //
        status.textContent = "Verifying archive '" + fileName + "'";
        let element = document.createElement("p");
        element.id = "playHashProgress";
        status.append(element);

        // Unpack archive //
        verifyDLHash(version, download, localTarget).then(() => {

            // Hash is correct //
            status.innerHTML = "";
            status.textContent = "Unpacking archive '" + fileName + "'";

            console.log("beginning unpacking");

            unpackRelease(installPath, download.folderName, localTarget).then(
                () => {

                    assert(fs.existsSync(path.join(installPath, download.folderName)));
                    console.log("unpacking completed");
                    
                    onThriveFolderReady(version, download);

                }, (error) => {

                    // Fail //
                    status.textContent = "Unpacking failed, File '" + fileName +
                        "' is invalid? " + error;

                    fs.unlinkSync(localTarget);
                    
                });
            

        }, () => {

            // Fail //
            status.textContent = "Hash for file '" + fileName + "' is invalid "+
                "(download corrupted or wrong file was downloaded) please try again";
        });
    });
}

//! Called when the play button is pressed
function playPressed(){

    // Cannot be downloading already //
    assert(playModalQuitDLCancel == null);
    currentDLCanceled = false;
    
    // Open play modal thing
    playModal.show();

    assert(playButtonText.dataset.selectedID);

    let version = versionInfo.getVersionByID(playButtonText.dataset.selectedID);

    assert(version);

    let download = versionInfo.getDownloadForPlatform(version.id);

    assert(download);

    console.log("Playing thrive version: " + version.releaseNum);

    let playBox = document.getElementById("playModalContent");

    playBox.innerHTML = "Playing Thrive " + version.releaseNum +
        "<p id='playingInternalP'>Downloading: " + download.url +
        "</p><div id='dlProgress'></div>";

    let fileName = download.fileName;

    assert(fileName);

    const localTarget = dlPath + fileName;
    
    if(fs.existsSync(localTarget)){

        console.log("already exists: " + fileName);
        onDLFileReady(version, download, fileName);
        return;
    }

    mkdirp(dlPath, function (err){
        
        if(err){
            
            console.error(err)
            alert("failed to create dl directory");
            return;
            
        }

        let dlFailCallback = (error) => {

            if(fs.existsSync(localTarget)){
                
                fs.unlinkSync(localTarget);
            }

            let status = document.getElementById("dlProgress");

            if(status){

                status.textContent = "Download Failed! " + error;
            }
        }

        let dataObj = {
            remoteFile: download.url,
            localFile: localTarget,
            
            onProgress: function (received, total){
                
                let percentage = (received * 100) / total;
                let status = document.getElementById("dlProgress");

                if(status){

                    status.textContent = percentage.toFixed(2) + "% | " + received +
                        " bytes out of " + total + " bytes."
                }
            }
        };

        downloadFile(dataObj).then(function(contentType){

            if(currentDLCanceled){

                // It was canceled //
                console.log("dl was canceled by user");

                if(fs.existsSync(localTarget))
                    fs.unlinkSync(localTarget);
                return;
            }
            
            if(![ "application/x-7z-compressed",
                  "application/zip",
                  "application/octet-stream"].includes(contentType))
            {
                dlFailCallback("download type is wrong: " + contentType);
                return;
            }

            console.log("Successfully downloaded");
            // No longer need to cancel
            playModalQuitDLCancel = null;

            let status = document.getElementById("playingInternalP");
            status.textContent = "Successfully downloaded " + version.releaseNum;
            
            onDLFileReady(version, download, fileName);
            
            
        }, function(error){

            dlFailCallback("Download failed: " + error);
        });

        // This object is for aborting a download
        assert(dataObj.reqObj);

        playModalQuitDLCancel = dataObj.reqObj;
        
    });

}

playButtonText.addEventListener("click", function(event){

    console.log("play clicked");
    playPressed();
});

let playComboPopup = document.getElementById("playComboPopup");

playComboPopup.addEventListener("click", function(event){

    console.log("open combo popup");
    
});

var playComboAllChoices = null;

//! Called once version info is loaded
function updatePlayButton(){

    playButtonText.textContent = "Processing Version Data...";

    let version = versionInfo.getRecommendedVersion();

    assert(version.stable);

    let dl = versionInfo.getDownloadForPlatform(version.id);
    
    // If this is null then we should let the user know that there was no 
    // preferred version
    assert(dl);
    
    // Verify retrieve logic
    assert(versionInfo.getCurrentPlatform().os == versionInfo.getPlatformByID(dl.os).os);
    
    playButtonText.textContent = "Play " + version.releaseNum +
        (version.stable ? "(Stable)" : "");

    playButtonText.dataset.selectedID = version.id;


    // Dump the other versions to be selected in the combo box thing //
    let options = versionInfo.getAllValidVersions();

    playComboAllChoices = options;

    console.log("All valid versions: " + options.length);
    
}

let newsContent = document.getElementById("newsContent");

let devForumPosts = document.getElementById("devForumPosts");

//
// Starts loading the news and shows them once loaded
//
function loadNews(){

    retrieveNews.retrieveNews(function(news, devposts){

        assert(news);
        assert(devposts);

        if(news.error){

            newsContent.textContent = news.error;
            
        } else {
            
            assert(news.htmlNodes);
            
            newsContent.innerHTML = "";
            newsContent.append(news.htmlNodes);
        }

        if(devposts.error){

            devForumPosts.textContent = devposts.error;
            
        } else {

            assert(devposts.htmlNodes);

            devForumPosts.innerHTML = "";
            devForumPosts.append(devposts.htmlNodes);
        }

    });
}

// Clear news and start loading them

if(fetchNewsFromWeb){
    
    newsContent.textContent = "Loading...";
    devForumPosts.textContent = "Loading...";

    loadNews();
    
} else {

    newsContent.textContent = "Web content is disabled.";
    devForumPosts.textContent = "Web content is disabled.";
}


// Links button 
let linksButton = document.getElementById("linksButton");

linksButton.addEventListener("click", function(event){

    linksModal.show();
});









