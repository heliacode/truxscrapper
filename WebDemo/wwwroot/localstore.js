/* eslint-disable  @typescript-eslint/no-explicit-any, @typescript-eslint/no-this-alias */
// noinspection JSUnfilteredForInLoop

const jsonStore = {};

export function read(key) {
  return jsonStore[key] ??= JSON.parse(localStorage[key] ?? 'null');
}

export function write(key, value) {
  return (localStorage[key] = JSON.stringify(value), jsonStore[key] = value);
}

export function ensured(key, value) {  
  jsonStore[key] ??= JSON.parse(localStorage[key] ?? 'null') ?? value;

  if (!(key in localStorage)) {
    localStorage[key] = JSON.stringify(jsonStore[key])
  }
  
  return jsonStore[key];
    
}