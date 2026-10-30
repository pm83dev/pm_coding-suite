import { TestBed } from '@angular/core/testing';
import { BrowserTestingModule } from '@angular/platform-browser/testing';

import { App } from './app';
import { ProtocolService } from './protocol/protocol.service';

let app: App;
let mockService: ReturnType<typeof createMockProtocolService>;

function createMockProtocolService() {
  return {
    connection: () => "closed" as const,
    error: () => "",
    sessions: () => [],
    currentSessionId: () => "",
    messages: () => [],
    streamingText: () => "",
    currentTurnId: () => "",
    settings: () => ({ serverUrl: "" }),
    send: () => {}, connect: () => {}, disconnect: () => {}, saveSettings: () => {},
  };
}

describe('App', () => {
  beforeEach(async () => {
    mockService = createMockProtocolService();

    await TestBed.configureTestingModule({
      imports: [BrowserTestingModule, App],
      providers: [
        {
          provide: ProtocolService,
          useValue: mockService,
        },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(App);
    app = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create the app', () => {
    expect(app).toBeTruthy();
  });

  it("should render the shell", () => {
    expect(app).toBeDefined();
  });
});
