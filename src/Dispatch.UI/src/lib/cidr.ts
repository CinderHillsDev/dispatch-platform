// Basic CIDR validation (IPv4 a.b.c.d/0-32, or an IPv6-ish addr/0-128) to stop mistyped entries.
const IPV4_CIDR = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\/(\d|[12]\d|3[0-2])$/;
const IPV6_CIDR = /^[0-9a-fA-F:]+\/(\d|[1-9]\d|1[01]\d|12[0-8])$/;

export function validCidr(v: string): boolean {
  if (IPV4_CIDR.test(v)) return v.split("/")[0].split(".").every((o) => Number(o) <= 255);
  return IPV6_CIDR.test(v);
}

export const PRIVATE_RANGES = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];
export const LOOPBACK_RANGES = ["127.0.0.1/32", "::1/128"];
export const ALLOW_ALL = ["0.0.0.0/0", "::/0"];
