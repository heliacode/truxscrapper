import { ensure, read, write } from "./localstore.js";
export class TrackingService {
    #cached = ensure("currentClient", () => this.askUserForClient());

    askUserForClient() {
        let client = "";

        while(!(client = prompt("Please enter your client name:")?.trim() ?? ""));

        return client;
    }

    getClientHistory(client) {
        const clientHistory = this.loadFromCache(client);
        return null;
    }
}