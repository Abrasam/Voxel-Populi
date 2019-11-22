import asyncio
import json
import random
from kademlia.node import Node
from kademlia.routing import RoutingTable

PYTHONASYNCIODEBUG = 1
TIMEOUT = 10  # RPC timeout.
K = 20
ALPHA = 3


def stub(func):
    assert(func.__name__[:4] == "ext_")  # sanity check.

    async def rpc_stub(self, node, *args):
        loop = asyncio.get_event_loop()
        msg = {"id": random.getrandbits(32), "node": self.id, "call": True, "rpc": func.__name__[4:], "args": args}
        self.transport.sendto(json.dumps(msg).encode("UTF-8"), node.addr)
        print("sent rpc " + json.dumps(msg) + " to " + str(node.addr))
        f = asyncio.Future()
        self.waiting[msg["id"]] = (f, loop.call_later(TIMEOUT, self._timeout, msg["id"]), node)
        await f
        return f.result()
    return rpc_stub


def rpc(func):
    def rpc_func(self, *args):
        return func(self, *args)
    return rpc_func


class KademliaServer(asyncio.DatagramProtocol):
    def __init__(self, id):
        self.id = id
        self.waiting = {}
        self.transport = None
        self.table = RoutingTable(self, K)

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        msg = json.loads(data.decode("UTF-8"))
        print(msg)
        node = self.table.get_node_if_contact(msg["node"])
        node = Node(msg["node"], addr) if node is None else node
        if msg["call"]:
            func = getattr(self,msg["rpc"], None)
            if func is None or not callable(func) or func.__name__ != "rpc_func":
                return
            res = json.dumps({"id": msg["id"], "node": self.id, "call": False, "rpc": msg["rpc"], "ret": func(*msg["args"])})
            self.transport.sendto(res.encode("UTF-8"), addr)
        else:
            if msg["id"] in self.waiting:
                self.waiting[msg["id"]][1].cancel()
                self.waiting[msg["id"]][0].set_result(msg["ret"])
                del self.waiting[msg["id"]]
        self._message_received(node)

    def _message_received(self, node):
        self.table.add_contact(node)
        # send values that need to be sent.

    def _timeout(self, msg_id):
        print("timed out")
        node = self.waiting[msg_id][2]
        self.waiting[msg_id][0].set_result(None)
        del self.waiting[msg_id]
        self.table.remove_contact(node)  # this is not correct to kademlia implementation, need to add 5 failure removal

    @stub
    async def ext_ping(self, node):
        pass

    @stub
    async def ext_find_node(self, node, node_id):
        pass

    @stub
    async def ext_find_value(self, node, key):
        pass

    @stub
    async def ext_store(self, node, key, value):
        pass

    @rpc
    def ping(self):
        return self.id

    @rpc
    def find_node(self, node_id):
        return list(map(lambda node: (node.id, node.ip, node.port), self.table.nearest_nodes_to(node_id)))

    @rpc
    def find_value(self, key):
        pass

    @rpc
    def store(self, key, value):
        pass

    async def lookup(self, key):
        nodes = self.table.nearest_nodes_to(key)
        queried = []
        while True:
            best = nodes[0]
            multicast = []
            unqueried = list(filter(lambda n: n not in queried, nodes))
            for i in range(0, min(ALPHA, len(unqueried))):
                node = unqueried.pop(0)
                multicast.append(node)
            queried += multicast
            res = await asyncio.gather(*[self.ext_find_node(n, key) for n in multicast])
            for i in range(0, len(res)):
                if res[i] is None:
                    continue
                nodes += list(map(lambda x: self.table.get_node_if_contact(x[0]) if self.table.get_node_if_contact(x[0]) is not None else Node(x[0], (x[1], x[2])), res[i]))
            nodes.sort(key=lambda n: n.id ^ key)
            nodes = nodes[:K]  # only keep K best.
            if best == nodes[0]:
                break
        return nodes

    async def bootstrap(self, node):
        self.table.add_contact(node)
        print("added contact")
        await self.lookup(self.id)
        self.table.refresh_buckets()  # should this be different?