import Foundation
import Network
import RouteMeterCore

final class Receiver {
    private let queue = DispatchQueue(label: "io.github.MatchalatteR.RouteMeter.receiver", qos: .utility)
    private var listener: NWListener?
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    private var timeouts: [ObjectIdentifier: DispatchWorkItem] = [:]
    private let storage: Storage
    private let receive: (Envelope) -> Void
    private let status: (String?) -> Void
    init(storage: Storage, receive: @escaping (Envelope) -> Void, status: @escaping (String?) -> Void) {
        self.storage = storage; self.receive = receive; self.status = status
    }
    func start() throws {
        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: storage.config.receiverPort)!)
        let listener = try NWListener(using: parameters)
        listener.stateUpdateHandler = { [weak self] state in
            switch state {
            case .ready: DispatchQueue.main.async { self?.status(nil) }
            case .failed: DispatchQueue.main.async { self?.status("Local port unavailable. Check for another RouteMeter instance.") }
            default: break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            guard let self, self.connections.count < 16 else { connection.cancel(); return }
            let id = ObjectIdentifier(connection)
            self.connections[id] = connection
            let timeout = DispatchWorkItem { [weak self, weak connection] in
                connection?.cancel(); self?.connections.removeValue(forKey: id); self?.timeouts.removeValue(forKey: id)
            }
            self.timeouts[id] = timeout
            self.queue.asyncAfter(deadline: .now() + 4, execute: timeout)
            connection.start(queue: self.queue)
            self.read(connection, buffer: Data())
        }
        self.listener = listener; listener.start(queue: queue)
    }
    func stop() {
        queue.async { [weak self] in
            self?.listener?.cancel()
            self?.connections.values.forEach { $0.cancel() }
            self?.connections.removeAll()
            self?.timeouts.values.forEach { $0.cancel() }
            self?.timeouts.removeAll()
        }
    }
    private func read(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 16_384) { [weak self] data, _, done, error in
            guard let self else { connection.cancel(); return }
            var buffer = buffer; if let data { buffer.append(data) }
            switch HTTPParser.parse(buffer) {
            case .failure(let code): self.respond(connection, code)
            case .request(let request): self.handle(connection, request)
            case .incomplete:
                if done || error != nil { self.finish(connection) }
                else { self.read(connection, buffer: buffer) }
            }
        }
    }
    private func handle(_ connection: NWConnection, _ request: HTTPRequest) {
        let host = request.headers["host"]?.lowercased()
        guard host == "127.0.0.1:\(storage.config.receiverPort)" || host == "localhost:\(storage.config.receiverPort)" else {
            respond(connection, 403); return
        }
        if request.method == "GET", request.path == storage.config.scriptPath {
            guard let script = try? storage.script() else { respond(connection, 500); return }
            respond(connection, 200, body: script, type: "application/javascript"); return
        }
        guard request.method == "POST", request.path == "/statistics" else { respond(connection, 404); return }
        guard HTTPParser.authorized(request.headers["x-route-meter-key"], key: storage.config.key) else {
            respond(connection, 403); return
        }
        guard let packet = try? JSONDecoder().decode(Envelope.self, from: request.body) else { respond(connection, 400); return }
        // Invalid/out-of-order statistics are rejected by the accumulator without changing UI state.
        DispatchQueue.main.async { self.receive(packet) }
        respond(connection, 204)
    }
    private func respond(_ connection: NWConnection, _ code: Int, body: Data = Data(), type: String = "text/plain") {
        let reason = [200:"OK",204:"No Content",400:"Bad Request",403:"Forbidden",404:"Not Found",413:"Payload Too Large",431:"Request Header Fields Too Large",500:"Internal Server Error"][code] ?? "Error"
        var response = Data("HTTP/1.1 \(code) \(reason)\r\nContent-Type: \(type)\r\nContent-Length: \(body.count)\r\nCache-Control: no-store\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n\r\n".utf8)
        response.append(body)
        connection.send(content: response, completion: .contentProcessed { [weak self] _ in self?.finish(connection) })
    }
    private func finish(_ connection: NWConnection) {
        let id = ObjectIdentifier(connection)
        timeouts.removeValue(forKey: id)?.cancel()
        connections.removeValue(forKey: id)
        connection.cancel()
    }
}
