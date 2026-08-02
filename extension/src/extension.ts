import * as vscode from 'vscode';
import {
	acceptEditProposal,
	handleChatRequest,
	rejectEditProposal,
	setEditProposalProvider,
} from './chatParticipant';
import { disposeAgentProcess } from './agentProcess';
import { EditProposalProvider, PROPOSED_EDIT_SCHEME } from './editProposalProvider';

export function activate(context: vscode.ExtensionContext) {
	// ─── Chat Participant Registration ────────────────────────────────────────
	const participant = vscode.chat.createChatParticipant(
		'pmChat.chat',
		handleChatRequest,
	);

	// ─── Provider per il diff delle modifiche proposte dall'agent ────────────
	const proposalProvider = new EditProposalProvider();
	setEditProposalProvider(proposalProvider);

	context.subscriptions.push(
		participant,
		vscode.workspace.registerTextDocumentContentProvider(PROPOSED_EDIT_SCHEME, proposalProvider),
		vscode.commands.registerCommand('pmChat.acceptEditProposal', acceptEditProposal),
		vscode.commands.registerCommand('pmChat.rejectEditProposal', rejectEditProposal),
		vscode.commands.registerCommand('pmChat.restartAgent', () => {
			// "Tasto di emergenza": uccide DAVVERO il processo pm-code.exe (non solo lo stato
			// della conversazione, come fa il reset soft su "New Chat") — utile se il processo
			// si è pianta o è rimasto in uno stato anomalo. Riparte pulito al prossimo messaggio.
			disposeAgentProcess();
			vscode.window.showInformationMessage('PM Code Agent riavviato — ripartirà al prossimo messaggio @pm.');
		}),
	);

	// eslint-disable-next-line @typescript-eslint/no-explicit-any
	(globalThis as any).console?.log('[PM Chat Participant] Active');
}

export function deactivate() {
	disposeAgentProcess();
}
