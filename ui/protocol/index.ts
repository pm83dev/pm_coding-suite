// Protocollo v1 - tipi condivisi

/** Messaggi in ingresso */
export interface InboundMessage {
  /** Nome del comando (es. "login", "uploadFile") */
  cmd: string;
  /** Payload specifico per il comando
   *  - login: { user: string; pwd: string }
   *  - uploadFile: { fileName: string; fileContent: string }
   *  - ...
   */
  payload: any;
}

/** Messaggi in uscita */
export interface OutboundMessage {
  /** Nome del comando risposto */
  cmd: string;
  /** Eventuali dati di risposta */
  data?: any;
}