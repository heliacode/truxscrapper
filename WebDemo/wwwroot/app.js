import { HubConnectionBuilder } from "./signalr.min.js"

const emptyList = `<div class="empty-list text-gray-500 py-6 text-center">No data to show. Please, add some tracking numbers</div>`;

let currentAborter = new AbortController();

export async function suscribeToClientEvents() {
    const clientName = getCurrentClient();

    if (!clientName) { showNotification('Error', 'Client name is required to proceed'); return; }

    document.getElementById('userName').textContent = `(${clientName ?? 'Guess'})`;

    try {
        connection.off("Update", receiveUpdates);
    } catch {

    }
    connection.on("Update", receiveUpdates);

    connection.invoke('UpdateConnectionIdAsync', clientName, getClientTrackingNumbers(clientName)).then();
}

// TODO: Implement last update from local storage
function receiveUpdates({ trackingNumber, history }) {
    console.log("Received status history:", history);

    const clientName = getCurrentClient();
    const trackingNumbers = getClientTrackingNumbersLogs(clientName);
    trackingNumbers[trackingNumber] = history;
    localStorage.setItem(clientNameAsId(getCurrentClient()), JSON.stringify(trackingNumbers));

    console.log("Saved status history:", trackingNumbers);

    const list = document.getElementById('freightList');
    tryRemoveEmptyList();

    //remove empty list message in case of history is not empty
    if (history.length > 0) {
        createListContainer(trackingNumber);
        updateListContainer(trackingNumber, history ?? []);
    }
    else {
        removeListContainer(trackingNumber);
    }

    // If there are no items in the list, show the empty list message
    if (list.children.length === 0) {
        list.innerHTML = emptyList;
    }

    showNotification('Status History Updated', `Received ${history.length} status updates`);
}

function createListContainer(trackingNumber)
{
    if(document.querySelector(`div[data-tracking-number="${trackingNumber}"]`)) return;

    document.getElementById('freightList').insertAdjacentHTML('afterbegin', 
        `<div data-tracking-number="${trackingNumber}"]>
            <div class="group-header">
                <span class="material-icons text-gray-500">local_shipping</span>
                ${trackingNumber}
            </div>
            <md-list>
                <md-list-item>
                    <md-circular-progress slot="start" four-color indeterminate></md-circular-progress>
                    Retrieving items...
                </md-list-item>
            </md-list>
        </div>`);
}

function updateListContainer(trackingNumber, history) { 
    document.querySelector(`div[data-tracking-number="${trackingNumber}"] md-list`)
            .innerHTML = history.sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp))
                                .map(i => mapUpdate(i, trackingNumber)).join('');
}

function removeListContainer(trackingNumber) {
    document.querySelector(`div[data-tracking-number="${trackingNumber}"]`)?.remove();
}

function mapUpdate(update) {

    const [location, company] = update.location.split(':').map(s => s.trim());

    return `<md-divider></md-divider>
    <md-list-item>
        <div slot="start" class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
            <span class="material-icons">local_shipping</span>
        </div>
        <div slot="headline" class="second-font font-bold">${company}</div>
        <div slot="supporting-text">${location}</div>
        <div slot="end" class="flex flex-col gap-2 items-center">
            <span class="text-xs text-gray-500">${update.timestamp}</span>
            <span class="px-3 py-1 rounded-full text-xs font-medium status-in-transit">${update.statusCode}</span>
        </div>
    </md-list-item>`;
}

function showNotification(title, message) {
    console.log(`${title}: ${message}`);
}

document.addEventListener('DOMContentLoaded', () => {

    const trackingNumbers = getClientTrackingNumbersLogs(getCurrentClient());

    if (trackingNumbers && Object.keys(trackingNumbers).length > 0) {        
        tryRemoveEmptyList();
        Object.entries(trackingNumbers).forEach(([num, history]) => {
            createListContainer(num);
            updateListContainer(num, history ?? [])
            // Optionally, load cached history here if you implement it
        });
    } else {
        tryAddEmptyList();
    }

    document.getElementById('addTrackingNumbers').addEventListener('click', async () => {
        try {
            const clientName = getCurrentClient(),
                trackingNumbers = addCurrentClientTrackingNumbers();

            console.log('Requesting latest status history...');

            requestUpdates(clientName, trackingNumbers).then();

        } catch (err) {
            console.error("Error requesting status history:", err);
            showNotification('Error', 'Failed to request status history');
        }
    });

});

const connection = new HubConnectionBuilder()
    .withUrl("/ordertracker")
    .build();

connection.start().then(async () => {

    console.log("Connected to SignalR hub");

    await suscribeToClientEvents();

}).catch(err => console.error("Error connecting to SignalR hub:", err));

let requestedFirst = false;

const TIMEOUT = 5 * 60 * 1000;

async function requestUpdates(clientName, trackingNumbers) {


    if (!trackingNumbers.length) {
        tryAddEmptyList()
        return;
    }
    
    tryRemoveEmptyList();

    trackingNumbers.forEach(num => createListContainer(num));

    console.log(`Retrieveing status for ${trackingNumbers} (Client: ${clientName})`)

    if (requestedFirst) {
        currentAborter.abort();
        currentAborter = new AbortController();
        console.log(`Creating new request`)
    }

    requestedFirst = true;

    const signal = currentAborter.signal;

    while (!signal.aborted) {
        connection.invoke('UpdateConnectionIdAsync', clientName, trackingNumbers).then();
        await delay(TIMEOUT, signal);
    }
}

function getCurrentClient() {
    if (localStorage.currentClient) return localStorage.currentClient;
    let clientName = null;
    while (!(clientName ??= prompt('Enter your name:')?.trim()));
    return localStorage.currentClient = clientName;
}

function getClientTrackingNumbers(client) {
    return Object.keys(getClientTrackingNumbersLogs(client));
}

function getClientTrackingNumbersLogs(client) {
    if (!client) return [];
    const clientStoreName = clientNameAsId(client);
    return JSON.parse(localStorage.getItem(clientStoreName)) || {};
}

function clientNameAsId(client) {
    return client.replaceAll(/[^\w]+/g, '_') + "_logs";
}

export function addCurrentClientTrackingNumbers() {
    return addClientTrackingNumbers(getCurrentClient(), prompt('Enter tracking numbers (comma separated):'));
}

function addClientTrackingNumbers(client, newNumbers) {
    if (!client || !newNumbers) return false;

    const clientStoreName = clientNameAsId(client);

    const trackingNumbers = JSON.parse(localStorage.getItem(clientStoreName)) || {};
    newNumbers = newNumbers.split(',').map(num => num.trim()).filter(num => num !== '');

    newNumbers.forEach(num => trackingNumbers[num] ??= []);

    localStorage.setItem(clientStoreName, JSON.stringify(trackingNumbers));

    return newNumbers;
}

function removeTrackingNumbers(client, trackingNumbers) {
    if (!client || !trackingNumbers) return false;

    const clientStoreName = clientNameAsId(client);
    const storedNumbers = JSON.parse(localStorage.getItem(clientStoreName)) || [];

    const numbersToRemove = trackingNumbers.split(',').map(num => num.trim()).filter(num => num !== '');
    const updatedNumbers = storedNumbers.filter(num => !numbersToRemove.includes(num));

    localStorage.setItem(clientStoreName, JSON.stringify(updatedNumbers));
    return updatedNumbers;
}

async function delay(/** @param {Number} ms*/ ms, /** @param {AbortSignal} signal */ signal) {
    return new Promise((resolve, reject) => {
        if (signal?.aborted) {
            reject(new DOMException('Delay aborted', 'AbortError'));
            return;
        }
        signal?.addEventListener('abort', /** @param {AbortSignalEventMap} e */ e => {
            reject(new DOMException('Delay aborted', 'AbortError'));
        });
        setTimeout(resolve, ms);
    }, ms);
}

function tryAddEmptyList(){
    const list = document.getElementById('freightList');
    if(list.childNodes.length > 0 || list.querySelector('.empty-list')) return;
    list.innerHTML = emptyList;
}
function tryRemoveEmptyList(){
    document.querySelector('.empty-list')?.remove();
}
//<md-list id="freightList">
//            <!--<md-list-item>
//              <div slot="start" class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//                <span class="material-icons">local_shipping</span>
//              </div>
//              <div slot="headline" class="second-font font-bold">FRG-78945612</div>
//              <div slot="supporting-text">From: New York � To: Los Angeles</div>
//              <div slot="end" class="flex flex-col gap-2 items-center">
//                <span class="text-xs text-gray-500">Today, 10:30 AM</span><span class="px-3 py-1 rounded-full text-xs font-medium status-in-transit">
//                  In
//                  Transit
//                </span>
//              </div>
//            </md-list-item>
//            <md-divider></md-divider>
//            <md-list-item>
//              <div slot="start" class="p-3 w-12 h-12 rounded-full bg-green-100 text-green-600">
//                <span class="material-icons">check_circle</span>
//              </div>
//              <div slot="headline" class="second-font font-bold">FRG-78945612</div>
//              <div slot="supporting-text">From: New York � To: Los Angeles</div>
//              <div slot="end" class="flex flex-col gap-2 items-center">
//                <span class="text-xs text-gray-500">Today, 10:30 AM</span>
//                <span class="px-3 py-1 rounded-full text-xs font-medium status-delivered">Delivered</span>
//              </div>
//            </md-list-item>
//            <md-divider></md-divider>
//            <md-list-item>
//              <div slot="start" class="p-3 w-12 h-12 rounded-full bg-yellow-100 text-yellow-600">
//                <span class="material-icons">schedule</span>
//              </div>
//              <div slot="headline" class="second-font font-bold">FRG-78945612</div>
//              <div slot="supporting-text">From: New York � To: Los Angeles</div>
//              <div slot="end" class="flex flex-col gap-2 items-center">
//                <span class="text-xs text-gray-500">Today, 10:30 AM</span>
//                <span class="px-3 py-1 rounded-full text-xs font-medium status-pending">Pending</span>
//              </div>
//            </md-list-item>
//            <md-divider></md-divider>
//            <md-list-item ripple>
//              <div slot="start" class="p-3 w-12 h-12 rounded-full bg-yellow-100 text-yellow-600">
//                <span class="material-icons">schedule</span>
//              </div>
//              <div slot="headline" class="second-font font-bold">FRG-78945612</div>
//              <div slot="supporting-text">From: New York � To: Los Angeles</div>
//              <div slot="end" class="flex flex-col gap-2 items-center">
//                <span class="text-xs text-gray-500">Today, 10:30 AM</span>
//                <span class="px-3 py-1 rounded-full text-xs font-medium status-pending">Pending</span>
//              </div>
//            </md-list-item>
//            <md-divider></md-divider>
//            <md-list-item>
//              <div slot="start" class="p-3 w-12 h-12 rounded-full bg-red-100 text-red-600">
//                <span class="material-icons">cancel</span>
//              </div>
//              <div slot="headline" class="second-font font-bold">FRG-78945612</div>
//              <div slot="supporting-text">From: New York � To: Los Angeles</div>
//              <div slot="end" class="flex flex-col gap-2 items-center">
//                <span class="text-xs text-gray-500">Today, 10:30 AM</span>
//                <span class="px-3 py-1 rounded-full text-xs font-medium status-cancelled">Cancelled</span>
//              </div>
//            </md-list-item>-->
//          </md-list>

//          <!-- < div class="p-4 hover:bg-gray-50 cursor-pointer card-hover ripple" >
//    <div class="flex items-center">
//        <div class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//            <span class="material-icons">local_shipping</span>
//        </div>
//        <div class="ml-4 flex-1 justify-between">
//            <h3 class="font-medium">FRG-78945612</h3>
//            <p class="text-sm text-gray-600">From: New York � To: Los Angeles</p>
//        </div>
//        <div class="flex flex-col gap-2 items-center">
//            <span class="text-xs text-gray-500">Today, 10:30 AM</span><span
//                class="px-3 py-1 rounded-full text-xs font-medium status-in-transit">In
//                Transit</span>
//        </div>
//    </div>
//          </div >
//          <div class="p-4 hover:bg-gray-50 cursor-pointer card-hover ripple">
//              <div class="flex items-center">
//                  <div class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//                      <span class="material-icons">local_shipping</span>
//                  </div>
//                  <div class="ml-4 flex-1 justify-between">
//                      <h3 class="font-medium">FRG-78945612</h3>
//                      <p class="text-sm text-gray-600">From: New York � To: Los Angeles</p>
//                  </div>
//                  <div class="flex flex-col gap-2 items-center">
//                      <span class="text-xs text-gray-500">Today, 10:30 AM</span><span
//                          class="px-3 py-1 rounded-full text-xs font-medium status-in-transit">In
//                          Transit</span>
//                  </div>
//              </div>
//          </div>
//          <div class="p-4 hover:bg-gray-50 cursor-pointer card-hover ripple">
//              <div class="flex items-center">
//                  <div class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//                      <span class="material-icons">local_shipping</span>
//                  </div>
//                  <div class="ml-4 flex-1 justify-between">
//                      <h3 class="font-medium">FRG-78945612</h3>
//                      <p class="text-sm text-gray-600">From: New York � To: Los Angeles</p>
//                  </div>
//                  <div class="flex flex-col gap-2 items-center">
//                      <span class="text-xs text-gray-500">Today, 10:30 AM</span><span
//                          class="px-3 py-1 rounded-full text-xs font-medium status-in-transit">In
//                          Transit</span>
//                  </div>
//              </div>
//          </div> -->
//          < !-- < div class="p-4 hover:bg-gray-50 cursor-pointer card-hover ripple" >
//    <div class="flex items-center">
//        <div class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//            <span class="material-icons">local_shipping</span>
//        </div>
//        <div class="ml-4 flex-1">
//            <div class="flex items-center justify-between">
//                <h3 class="font-medium">FRG-78945612</h3>
//                <span class="text-xs text-gray-500">Today, 10:30 AM</span>
//            </div>
//            <p class="text-sm text-gray-600">From: New York � To: Los Angeles</p>
//        </div>
//        <div class="ml-4">
//            <span class="px-3 py-1 rounded-full text-xs font-medium status-in-transit">In Transit</span>
//        </div>
//    </div>
//          </div >
//          <div class="p-4 hover:bg-gray-50 cursor-pointer card-hover ripple">
//              <div class="flex items-center">
//                  <div class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//                      <span class="material-icons">local_shipping</span>
//                  </div>
//                  <div class="ml-4 flex-1">
//                      <div class="flex items-center justify-between">
//                          <h3 class="font-medium">FRG-12345678</h3>
//                          <span class="text-xs text-gray-500">Yesterday, 3:45 PM</span>
//                      </div>
//                      <p class="text-sm text-gray-600">From: Chicago � To: Miami</p>
//                  </div>
//                  <div class="ml-4">
//                      <span class="px-3 py-1 rounded-full text-xs font-medium status-delivered">Delivered</span>
//                  </div>
//              </div>
//          </div>
//          <div class="p-4 hover:bg-gray-50 cursor-pointer card-hover ripple">
//              <div class="flex items-center">
//                  <div class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//                      <span class="material-icons">local_shipping</span>
//                  </div>
//                  <div class="ml-4 flex-1">
//                      <div class="flex items-center justify-between">
//                          <h3 class="font-medium">FRG-98765432</h3>
//                          <span class="text-xs text-gray-500">May 15, 2023, 9:15 AM</span>
//                      </div>
//                      <p class="text-sm text-gray-600">From: Seattle � To: Boston</p>
//                  </div>
//                  <div class="ml-4">
//                      <span class="px-3 py-1 rounded-full text-xs font-medium status-pending">Pending</span>
//                  </div>
//              </div>
//          </div>
//          <div class="p-4 hover:bg-gray-50 cursor-pointer card-hover ripple">
//              <div class="flex items-center">
//                  <div class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//                      <span class="material-icons">local_shipping</span>
//                  </div>
//                  <div class="ml-4 flex-1">
//                      <div class="flex items-center justify-between">
//                          <h3 class="font-medium">FRG-45612378</h3>
//                          <span class="text-xs text-gray-500">May 10, 2023, 11:20 AM</span>
//                      </div>
//                      <p class="text-sm text-gray-600">From: Denver � To: Atlanta</p>
//                  </div>
//                  <div class="ml-4">
//                      <span class="px-3 py-1 rounded-full text-xs font-medium status-cancelled">Cancelled</span>
//                  </div>
//              </div>
//          </div>
//          <div class="p-4 hover:bg-gray-50 cursor-pointer card-hover ripple">
//              <div class="flex items-center">
//                  <div class="p-3 w-12 h-12 rounded-full bg-blue-100 text-blue-600">
//                      <span class="material-icons">local_shipping</span>
//                  </div>
//                  <div class="ml-4 flex-1">
//                      <div class="flex items-center justify-between">
//                          <h3 class="font-medium">FRG-32165498</h3>
//                          <span class="text-xs text-gray-500">May 8, 2023, 2:00 PM</span>
//                      </div>
//                      <p class="text-sm text-gray-600">From: Houston � To: Phoenix</p>
//                  </div>
//                  <div class="ml-4">
//                      <span class="px-3 py-1 rounded-full text-xs font-medium status-delivered">Delivered</span>
//                  </div>
//              </div>
//          </div> -->