
import Vue from './vue/vue.esm.browser.min.js';
import './vue/vuetify.min.js';
import local from './localstore.js';
import { HubConnectionBuilder } from "./signalr.min.js"

export default function app() {
  new Vue({
    el: '#app',
    vuetify: new Vuetify({
      theme: {
        dark: window.matchMedia?.('(prefers-color-scheme: dark)')?.matches ?? true,
        options: {
          themeCache: {
            get: key => localStorage.getItem(key),
            set: (key, value) => localStorage.setItem(key, value),
          },
        }
      }
    }),
    data: {
      currentClient: null,
      signalRConnection: new HubConnectionBuilder().withUrl("/ordertracker").build()
    },
    methods: {
      async requestUpdates() {
        if (this.getCurrentClient()) {
          await this.signalRConnection.invoke("Update", this.currentClient);
        }
      },
      getCurrentClient() {
        if (localStorage.currentClient) return localStorage.currentClient;
        let clientName = null;
        while (!(clientName ??= prompt('Enter your name:')?.trim()));
        return localStorage.currentClient = clientName;
      },
      
      async connect() {
        const clientName = await this.getCurrentClient();

        if (!clientName) { showNotification('Error', 'Client name is required to proceed'); return; }

        document.getElementById('userName').textContent = `(${clientName ?? 'Guess'})`;

        this.signalRConnection.on("Update", this.receiveUpdates);

        this.signalRConnection.invoke('UpdateConnectionIdAsync', clientName, getClientTrackingNumbers(clientName)).then();
      }
    }
  });
}